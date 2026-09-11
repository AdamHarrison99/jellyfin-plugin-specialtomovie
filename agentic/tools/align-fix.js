// Places the cross-link icon from the browser console, on a server running a build whose own
// placement is wrong -- without installing anything.
//
// It carries the same rules as Web/specialtomovie.js: align to the nearest sibling the row actually
// draws, only when that sibling shares the line, and re-measure whenever the row moves under it.
// Written for v2.0.0, which measures once and keeps that measurement however the row changes after.
//
// Paste the whole file into the DevTools console on a detail page. It lasts until the tab reloads.
// Stop it with __stmAlignFix.stop(), and read __stmAlignFix.changes for how often it re-placed.
//
// Against v2.0.0 this is the evidence that the shipped fix is the right one, on a real row with
// whatever logo CSS, theme and other plugins the install has:
//   node test-layout.js path/to/v2.0.0/specialtomovie.js ../align-fix.js
//
// It duplicates the algorithm rather than sharing it, because it has to run beside a copy of the
// script it is correcting. Change it with Web/specialtomovie.js or it stops being evidence.
(function () {
    'use strict';

    if (window.__stmAlignFix) { window.__stmAlignFix.stop(); }

    var GAP_VAR = '--stm-icon-gap';
    var SHIFT_VAR = '--stm-icon-shift';
    var KEY_ATTR = 'data-stm-fixkey';
    var MAX_SHIFT = 40;
    var SETTLE_CHECKS = [400, 1200, 3000];
    var changes = 0;

    var drawn = function (el) {
        return el.getBoundingClientRect().width > 0 &&
            window.getComputedStyle(el).visibility !== 'hidden';
    };

    // The nearest element before the icon that the row actually draws. A hidden or empty sibling
    // has no position to sit against.
    var rowReference = function (anchor) {
        var node = anchor.previousElementSibling;
        while (node) {
            if (drawn(node)) { return node; }
            node = node.previousElementSibling;
        }
        return null;
    };

    // A zero-height rect is a baseline, and a logo is centred on it.
    var centre = function (rect) {
        return rect.height > 0 ? rect.top + rect.height / 2 : rect.top;
    };

    var follows = function (theirs, mine, rtl) {
        return rtl ? mine.right <= theirs.left + 0.5 : mine.left >= theirs.right - 0.5;
    };

    // One line holds both when the icon follows that link, or their boxes overlap vertically.
    // Either test alone fails a row type; see agentic/ARCHITECTURE.md.
    var sameLine = function (theirs, mine, rtl) {
        return follows(theirs, mine, rtl) ||
            (mine.top <= theirs.bottom + 2 && theirs.top <= mine.bottom + 2);
    };

    var layoutKey = function (anchor, reference, container) {
        var box = container.getBoundingClientRect();
        var mine = anchor.getBoundingClientRect();
        var parts = [Math.round(box.width), Math.round(mine.left - box.left),
            Math.round(mine.top - box.top), Math.round(mine.width)];

        if (reference) {
            var theirs = reference.getBoundingClientRect();
            parts.push(Array.prototype.indexOf.call(container.children, reference),
                Math.round(theirs.left - box.left), Math.round(theirs.top - box.top),
                Math.round(theirs.width), Math.round(theirs.height));
        }

        return parts.join(',');
    };

    // The gap the row leaves between its own links, excluding the two either side of the icon.
    var rowGap = function (container, self) {
        var links = container.querySelectorAll('a');
        var gaps = [];

        for (var i = 1; i < links.length; i++) {
            if (links[i] === self || links[i - 1] === self) { continue; }
            var a = links[i - 1].getBoundingClientRect();
            var b = links[i].getBoundingClientRect();
            if (Math.abs(b.top - a.top) < 2 && b.left >= a.right - 0.5) { gaps.push(b.left - a.right); }
        }

        if (!gaps.length) { return null; }
        gaps.sort(function (x, y) { return x - y; });
        return gaps[Math.floor(gaps.length / 2)];
    };

    // What the correction was measured against, including the values written for it -- so a
    // correction the installed script writes over the top re-measures as a moved row does.
    var stateOf = function (anchor, reference, container) {
        return layoutKey(anchor, reference, container) + '|' +
            anchor.style.getPropertyValue(SHIFT_VAR) + '|' + anchor.style.getPropertyValue(GAP_VAR);
    };

    var place = function (anchor) {
        var container = anchor.parentElement;
        if (!container || !container.querySelectorAll) { return; }

        var mine = anchor.getBoundingClientRect();
        if (!mine.width && !mine.height) { return; }

        var reference = rowReference(anchor);
        if (anchor.getAttribute(KEY_ATTR) === stateOf(anchor, reference, container)) { return; }

        var was = anchor.style.getPropertyValue(SHIFT_VAR);

        // A row holding only this link is spaced by whatever precedes it.
        if (!reference) {
            anchor.style.removeProperty(GAP_VAR);
            anchor.style.removeProperty(SHIFT_VAR);
            anchor.setAttribute(KEY_ATTR, stateOf(anchor, reference, container));
            return;
        }

        // Zeroed first, or the measurement reads back its own correction.
        anchor.style.setProperty(GAP_VAR, '0px');
        anchor.style.setProperty(SHIFT_VAR, '0px');

        var theirs = reference.getBoundingClientRect();
        mine = anchor.getBoundingClientRect();
        var rtl = window.getComputedStyle(container).direction === 'rtl';

        // A link on the line below is spaced and centred by that line, not by this one.
        if (sameLine(theirs, mine, rtl)) {
            var shift = centre(theirs) - centre(mine);
            if (isFinite(shift) && Math.abs(shift) <= MAX_SHIFT) {
                anchor.style.setProperty(SHIFT_VAR, (Math.round(shift * 100) / 100) + 'px');
            }

            var target = rowGap(container, anchor);
            if (target !== null && follows(theirs, mine, rtl)) {
                var distance = rtl ? theirs.left - mine.right : mine.left - theirs.right;
                var correction = target - distance;
                anchor.style.setProperty(GAP_VAR, (correction > 0.5 ? correction : 0) + 'px');
            }
        }

        anchor.setAttribute(KEY_ATTR, stateOf(anchor, reference, container));

        var now = anchor.style.getPropertyValue(SHIFT_VAR);
        if (now !== was) {
            changes++;
            console.log('[STM fix] placed against "' +
                (reference.textContent || '').trim().slice(0, 24) + '": ' +
                (was || 'unset') + ' -> ' + now);
        }
    };

    var pass = function () {
        try {
            [].slice.call(document.querySelectorAll('a.specialtomovie-link[href*="stm="]'))
                .filter(function (a) { return a.getClientRects().length > 0; })
                .forEach(place);
        } catch (err) {
            console.warn('[STM fix] pass failed', err);
        }
    };

    // Later than the installed script's own pass, so this runs on what it leaves behind.
    var pending = null;
    var schedule = function () {
        if (pending) { return; }
        pending = window.setTimeout(function () { pending = null; pass(); }, 150);
    };

    // A stylesheet arriving late restyles the row without touching the DOM.
    // The previous page's checks are dropped rather than added to: this page supersedes them, and a
    // console tool outlives many navigations, so an array that only ever grows is a leak.
    var timers = [];
    var settle = function () {
        timers.forEach(window.clearTimeout);
        timers = SETTLE_CHECKS.map(function (delay) { return window.setTimeout(schedule, delay); });
    };

    var observer = new MutationObserver(schedule);
    observer.observe(document.body, { childList: true, subtree: true, attributeFilter: ['class'] });
    window.addEventListener('resize', schedule);
    window.addEventListener('hashchange', settle);

    window.__stmAlignFix = {
        stop: function () {
            observer.disconnect();
            window.removeEventListener('resize', schedule);
            window.removeEventListener('hashchange', settle);
            timers.forEach(window.clearTimeout);
            if (pending) { window.clearTimeout(pending); }
            [].slice.call(document.querySelectorAll('[' + KEY_ATTR + ']'))
                .forEach(function (a) { a.removeAttribute(KEY_ATTR); });
            delete window.__stmAlignFix;
            console.log('[STM fix] stopped. Reload the page to return to the installed behaviour.');
        },
        recheck: pass,
        get changes() { return changes; }
    };

    pass();
    settle();
    console.log('[STM fix] running. It re-places the icon whenever the row changes. ' +
        'Navigate between a special and its movie to check both. Stop with __stmAlignFix.stop()');
})();
