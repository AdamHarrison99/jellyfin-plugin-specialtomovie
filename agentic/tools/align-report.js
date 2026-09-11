// Paste into the browser console on a detail page whose cross-link icon looks misplaced.
//
// The row this icon sits in is built by the web client, restyled by whatever logo CSS the user
// installed, and added to by other plugins - each at its own moment. A correction measured while
// that is still happening describes a row that no longer exists, which is what this reports:
//
//   drift        the correction the icon carries, against the correction the row asks for now.
//                0 = placed for the row as it stands. Anything else is the pixels it is out by,
//                and means something changed after the measurement without a re-measure.
//   row          every element in the row, hidden ones included: the box it occupies, and where
//                it is actually painted - hit-tested, never read from a rect.
//   re-measured  the same figures after forcing a fresh measurement. A drift that clears is a
//                stale measurement; one that survives is a measurement wrong on today's row.
//
// The report is copied to the clipboard. It names providers and link captions, and carries no
// server address, item id or user detail.
(async () => {
    const r1 = (n) => Math.round(n * 10) / 10;
    const shown = (el) => el.getClientRects().length > 0;
    const frames = () => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));

    const ours = [].slice.call(document.querySelectorAll('a[href*="stm="]')).find(shown);
    if (!ours) {
        console.warn('[STM] No visible cross-link on this page.');
        return;
    }

    const row = ours.parentElement;
    row.scrollIntoView({ block: 'center' });
    await frames();

    // Links other plugins add to this row. Names from Jellyfin-Enhanced.
    const PLUGIN = ['specialtomovie-link', 'letterboxd-link', 'seerr-link', 'arr-link',
        'arr-tag-link'];

    // Where an element is drawn, found by hit-testing its pixels. A logo anchor's own rect is
    // zero-height, and the icon's correction is a paint-only offset, so neither shows in a rect.
    const painted = (el) => {
        const b = el.getBoundingClientRect();
        const rb = row.getBoundingClientRect();
        const y0 = Math.max(0, rb.top - 60);
        const y1 = Math.min(window.innerHeight - 1, rb.bottom + 60);

        for (const dx of [2, 5, 9, 14, 20, 30]) {
            const x = b.left + dx;
            let top = null;
            let bottom = null;

            for (let y = y0; y <= y1; y += 0.5) {
                const hit = document.elementFromPoint(x, y);
                if (hit && el.contains(hit)) {
                    if (top === null) { top = y; }
                    bottom = y;
                }
            }

            if (top !== null) { return { centre: r1((top + bottom) / 2), height: r1(bottom - top) }; }
        }

        return null;
    };

    const pseudo = (el, which) => {
        const s = window.getComputedStyle(el, which);
        if (!s || s.content === 'none' || s.content === 'normal') { return null; }
        return { display: s.display, width: s.width, height: s.height,
            verticalAlign: s.verticalAlign, position: s.position, top: s.top, margin: s.margin,
            image: s.backgroundImage !== 'none' };
    };

    const hostOf = (el) => {
        if (el === ours) { return '(ours)'; }
        const href = el.getAttribute('href');
        if (!href) { return ''; }

        try {
            const u = new URL(href, location.href);
            return u.host === location.host
                ? '(this server)'
                : u.host + u.pathname.split('/').slice(0, 2).join('/');
        } catch (err) {
            return '?';
        }
    };

    // What the script itself uses: a zero-height rect is a baseline, and a logo is centred on it.
    const centre = (rect) => (rect.height > 0 ? rect.top + rect.height / 2 : rect.top);

    const describe = (el, index) => {
        const s = window.getComputedStyle(el);
        const b = el.getBoundingClientRect();
        const isShown = shown(el);
        const p = isShown ? painted(el) : null;

        return {
            index: index,
            tag: el.tagName.toLowerCase(),
            name: (el.getAttribute('aria-label') || el.getAttribute('title') ||
                el.textContent || '').trim().slice(0, 30),
            host: hostOf(el),
            role: el === ours ? 'OURS' : '',
            plugin: el !== ours && PLUGIN.some((c) => el.classList.contains(c)),
            classes: String(el.className || '').replace(/\s+/g, ' ').trim(),
            shown: isShown,
            style: { display: s.display, visibility: s.visibility, fontSize: s.fontSize,
                lineHeight: s.lineHeight, verticalAlign: s.verticalAlign, position: s.position,
                top: s.top, padding: s.padding, margin: s.margin },
            rect: { top: r1(b.top), left: r1(b.left), width: r1(b.width), height: r1(b.height) },
            before: pseudo(el, '::before'),
            after: pseudo(el, '::after'),
            children: [].slice.call(el.children).map((c) => {
                const cb = c.getBoundingClientRect();
                const cs = window.getComputedStyle(c);
                return { tag: c.tagName.toLowerCase(), w: r1(cb.width), h: r1(cb.height),
                    top: r1(cb.top), display: cs.display, verticalAlign: cs.verticalAlign };
            }),
            modelCentre: r1(centre(b)),
            paintedCentre: p ? p.centre : null,
            paintedHeight: p ? p.height : null
        };
    };

    const snapshot = (label) => {
        const items = [].slice.call(row.children).map(describe);
        const me = items.find((x) => x.role === 'OURS');
        const before = items.slice(0, items.indexOf(me));
        const drawn = before.filter((x) => x.rect.width > 0 && x.style.visibility !== 'hidden');
        const reference = drawn.length ? drawn[drawn.length - 1] : null;
        const applied = parseFloat(ours.style.getPropertyValue('--stm-icon-shift')) || 0;
        const paintedOff = (ref) => (me.paintedCentre !== null && ref && ref.paintedCentre !== null
            ? r1(me.paintedCentre - ref.paintedCentre)
            : null);

        return {
            label: label,
            shiftVar: ours.style.getPropertyValue('--stm-icon-shift'),
            sizeVar: ours.style.getPropertyValue('--stm-icon-size'),
            gapVar: ours.style.getPropertyValue('--stm-icon-gap'),
            measuredAgainst: ours.getAttribute('data-stm-matched'),
            alignsTo: reference ? reference.name + ' [' + reference.index + ']' : null,
            skipped: before.filter((x) => drawn.indexOf(x) === -1)
                .map((x) => x.name + ' [' + x.index + ', ' + x.style.display + ']'),
            drift: reference ? r1(reference.modelCentre - me.modelCentre - applied) : null,
            paintedAgainstReference: paintedOff(reference),
            items: items
        };
    };

    const asFound = snapshot('as found');

    // Drop the stored measurement and nudge the row, so the script measures it again.
    ours.removeAttribute('data-stm-matched');
    row.classList.add('stm-report');
    row.classList.remove('stm-report');
    await new Promise((r) => setTimeout(r, 1500));
    await frames();
    const remeasured = snapshot('after a forced re-measure');

    const findings = [];
    if (asFound.drift === null) {
        findings.push('Nothing is drawn before the icon in this row.');
    } else if (Math.abs(asFound.drift) > 1) {
        findings.push('The icon is out by ' + asFound.drift + 'px against ' + asFound.alignsTo +
            ': its correction was measured on a row that has changed since.');
    }
    if (asFound.shiftVar !== remeasured.shiftVar) {
        findings.push('Re-measuring changed the correction from ' + asFound.shiftVar + ' to ' +
            remeasured.shiftVar + '.');
    }
    if (Math.abs(remeasured.drift || 0) > 1) {
        findings.push('Still out by ' + remeasured.drift + 'px after re-measuring: the correction ' +
            'is wrong for this row, not merely stale.');
    }
    if (asFound.skipped.length) {
        findings.push('Not drawn, so skipped when choosing what to align to: ' +
            asFound.skipped.join(', ') + '.');
    }
    if (!findings.length) {
        findings.push('Placed for the row as it stands, before and after re-measuring.');
    }

    console.log('[STM] ' + findings.join('\n[STM] '));
    console.table([asFound, remeasured].map((s) => ({
        state: s.label, shift: s.shiftVar, 'aligns to': s.alignsTo,
        'drift (px)': s.drift, 'painted vs reference (px)': s.paintedAgainstReference
    })));
    console.table(asFound.items.map((x) => ({
        '#': x.index, tag: x.tag, name: x.name, host: x.host, role: x.role, shown: x.shown,
        display: x.style.display, plugin: x.plugin, modelCentre: x.modelCentre,
        paintedCentre: x.paintedCentre
    })));

    const json = JSON.stringify({ page: location.hash.split('?')[0],
        viewport: window.innerWidth + 'x' + window.innerHeight, findings: findings,
        asFound: asFound, remeasured: remeasured }, null, 1);

    if (typeof copy === 'function') {
        copy(json);
        console.log('[STM] Full report copied to the clipboard.');
    } else {
        console.log(json);
    }
})();
