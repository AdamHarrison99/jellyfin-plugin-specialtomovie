// Every emby-select in configPage.html must sit in a positioned parent, with its chevron drawn
// inside the select box and centred on it.
//
// Jellyfin's emby-select draws its own chevron in a .selectArrowContainer, which is
// position: absolute. An absolutely positioned box resolves against its nearest positioned
// ancestor, so a select whose parent is static loses its arrow to the page's top-right corner --
// where it reads as a stray control that does nothing. Three filter selects did exactly that
// until they were each wrapped in .pairs-select-wrapper.
//
// Holding the arrow is not enough. Jellyfin's .selectArrow carries margin-top: 1.2em at 1.7em to
// clear the label emby-select puts above a select. A select with no label text gets an empty label
// and the same drop, which put the three filter arrows on the bottom border of their boxes in
// v2.1.0. So the glyph itself must fall inside the select's box and sit on its vertical centre.
//
// This does not need Jellyfin: it builds what emby-select's attachedCallback builds (an empty
// .selectLabel before the select, a .selectArrowContainer appended to the parent) and applies
// Jellyfin's own emby-select rules, copied from jellyfin-web v12.0, ahead of the page's styles as
// the server would. The icon font is not loaded, so .material-icons gets its glyph box (1em square,
// line-height 1) from a stand-in rule.
//
//   node configpage-selects.js [path-to-configPage.html] [path-to-console-script]
//
// A second path is a console script pasted into the rendered page before measuring, which is how
// configpage-arrow-fix.js is checked against a released page that still has the fault.
//
// STM_ARROW_MODE=end|after chooses where the simulated arrow is placed: appended to the parent, as
// emby-select v12.0 does (the default), or next to the select. A console fix must pass under both.
//
// Exit 0 = every select holds its arrow, centred in its box; 1 = at least one does not,
// 2 = the page could not be read, or no browser was found.
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
const { requireBrowser } = require('./webclient-harness/browser');

// playwright-core is the harness's dependency, so it resolves from there rather than from here.
const HARNESS = path.join(__dirname, 'webclient-harness');
let chromium;
try {
    chromium = require(require.resolve('playwright-core', { paths: [HARNESS] })).chromium;
} catch (e) {
    console.error('playwright-core is missing. Run npm install in agentic/tools/webclient-harness.');
    process.exit(2);
}

const pageArg = process.argv[2] ||
    path.join(__dirname, '..', '..', 'Configuration', 'configPage.html');
if (!fs.existsSync(pageArg)) {
    console.error('cannot read the page under test: ' + pageArg);
    process.exit(2);
}

const fixArg = process.argv[3];
if (fixArg && !fs.existsSync(fixArg)) {
    console.error('cannot read the console script: ' + fixArg);
    process.exit(2);
}
const FIX = fixArg ? fs.readFileSync(fixArg, 'utf8') : null;
const browserPath = requireBrowser();

// Jellyfin's emby-select rules (jellyfin-web v12.0, src/elements/emby-select/emby-select.scss),
// flattened for ltr. The last rule stands in for the Material Icons font, which is not loaded here.
const JELLYFIN_CSS = [
    '.emby-select { display: block; margin: 0; margin-bottom: 0 !important; font-size: 110%;',
    '  box-sizing: border-box; width: 100%; padding: 0.5em 1.9em 0.5em 0.5em; }',
    '.selectContainer { margin-bottom: 1.8em; position: relative; }',
    '.selectLabel { display: block; margin-bottom: 0.25em; }',
    '.selectArrowContainer { position: absolute; top: 0.2em; right: 0.3em; color: inherit;',
    '  pointer-events: none; }',
    '.selectArrow { margin-top: 1.2em; font-size: 1.7em; }',
    '.material-icons { display: inline-block; line-height: 1; width: 1em; height: 1em; }'
].join('\n');

// What emby-select's attachedCallback builds around a select. It appends the arrow to the parent;
// the older next-to-the-select placement is still exercised with STM_ARROW_MODE=after.
const UPGRADE = (args) => {
    const css = document.createElement('style');
    css.textContent = args.css;
    document.head.insertBefore(css, document.head.firstChild);

    const made = [];
    document.querySelectorAll('select[is="emby-select"]').forEach((sel) => {
        sel.classList.add('emby-select-withcolor', 'emby-select');

        const label = document.createElement('label');
        label.className = 'selectLabel';
        label.htmlFor = sel.id;
        label.innerText = sel.getAttribute('label') || '';
        sel.parentNode.insertBefore(label, sel);

        const box = document.createElement('div');
        box.className = 'selectArrowContainer';
        box.innerHTML = '<div style="visibility:hidden;display:none;">0</div>' +
            '<span class="selectArrow material-icons keyboard_arrow_down" aria-hidden="true"></span>';
        made.push({ box: box, parent: sel.parentNode, after: sel });
    });

    made.forEach((m) => {
        if (args.mode === 'after') {
            m.parent.insertBefore(m.box, m.after.nextSibling);
        } else {
            m.parent.appendChild(m.box);
        }
    });
};

const MEASURE = () => {
    const out = [];
    document.querySelectorAll('select[is="emby-select"]').forEach((sel) => {
        const parent = sel.parentElement;
        const held = window.getComputedStyle(parent).position !== 'static';

        // Paired by order within the shared parent, which holds whichever way the arrow was placed.
        const kin = [].slice.call(parent.children);
        const selects = kin.filter((el) => el.tagName === 'SELECT');
        const arrows = kin.filter((el) => el.classList.contains('selectArrowContainer'));
        const arrow = arrows[selects.indexOf(sel)] || null;

        const glyph = arrow ? arrow.querySelector('.selectArrow') : null;
        const box = sel.getBoundingClientRect();
        const mine = glyph ? glyph.getBoundingClientRect() : null;

        // The glyph, not its container, must be inside the select box.
        const inside = !!mine && mine.left >= box.left - 1 && mine.right <= box.right + 1 &&
            mine.top >= box.top - 1 && mine.bottom <= box.bottom + 1;

        // Centred within a tenth of the box height. Only enforced on wrappers the page defines:
        // a Jellyfin .selectContainer lays out its own label, which this model does not style.
        const drift = mine ? (mine.top + mine.height / 2) - (box.top + box.height / 2) : NaN;
        const ours = !parent.classList.contains('selectContainer');
        const centred = !ours || Math.abs(drift) <= box.height * 0.1;

        const why = !held ? 'parent is static' : !mine ? 'no arrow' :
            !inside ? 'glyph outside the select box' : !centred ? 'glyph off centre' : '';

        out.push({
            id: sel.id || '(no id)',
            parent: parent.className || '(no class)',
            position: window.getComputedStyle(parent).position,
            ok: held && inside && centred,
            drift: isNaN(drift) ? 'n/a' : (drift >= 0 ? '+' : '') + drift.toFixed(1) + 'px',
            box: [Math.round(box.top), Math.round(box.bottom)],
            glyph: mine ? [Math.round(mine.top), Math.round(mine.bottom)] : ['none', 'none'],
            why: why
        });
    });

    return out;
};

(async () => {
    const browser = await chromium.launch({ executablePath: browserPath });
    const p = await browser.newPage();

    await p.setViewportSize({ width: 1400, height: 900 });
    await p.goto(pathToFileURL(path.resolve(pageArg)).href);
    await p.waitForTimeout(400);
    await p.evaluate(UPGRADE, { css: JELLYFIN_CSS, mode: process.env.STM_ARROW_MODE || 'end' });

    if (FIX) {
        await p.addScriptTag({ content: FIX });
        await p.waitForTimeout(300);
        console.log('(with ' + path.basename(fixArg) + ' pasted)');
    }

    const rows = await p.evaluate(MEASURE);
    let failed = 0;

    rows.forEach((r) => {
        if (!r.ok) { failed++; }
        console.log((r.ok ? '  PASS  ' : '  FAIL  ') + r.id.padEnd(22) +
            ' parent=' + r.parent.padEnd(24) + r.position.padEnd(10) +
            ' box y ' + r.box.join('-') + '  glyph y ' + r.glyph.join('-') +
            '  drift ' + r.drift + (r.why ? '  <- ' + r.why : ''));
    });

    console.log('\n' + (rows.length - failed) + ' of ' + rows.length +
        ' selects hold their arrow, centred inside the box');
    await p.close();
    await browser.close();
    process.exit(failed ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
