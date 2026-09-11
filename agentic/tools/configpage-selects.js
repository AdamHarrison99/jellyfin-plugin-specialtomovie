// Every emby-select in configPage.html must sit in a positioned parent.
//
// Jellyfin's emby-select draws its own chevron in a .selectArrowContainer, which is
// position: absolute. An absolutely positioned box resolves against its nearest positioned
// ancestor, so a select whose parent is static loses its arrow to the page's top-right corner --
// where it reads as a stray control that does nothing. Three filter selects did exactly that
// until they were each wrapped in .pairs-select-wrapper.
//
// This does not need Jellyfin: it builds the arrow the way emby-select does and checks the
// containing block the page is responsible for. Jellyfin's own .selectContainer rule is supplied
// here because the page relies on it without shipping it.
//
//   node configpage-selects.js [path-to-configPage.html] [path-to-console-script]
//
// A second path is a console script pasted into the rendered page before measuring, which is how
// configpage-arrow-fix.js is checked against a released page that still has the fault.
//
// STM_ARROW_MODE=after|end chooses where the simulated arrow is placed, since emby-select's own
// placement varies. A console script that repairs the page must pass under both.
//
// Exit 0 = every select holds its arrow, 1 = at least one would lose it,
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

// What emby-select builds around a select once it upgrades one. Its placement of the arrow varies,
// so both arrangements seen in the wild are exercised: next to the select, and appended at the end
// of the row after every select in it.
const UPGRADE = (mode) => {
    const css = document.createElement('style');
    css.textContent = '.selectContainer { position: relative; }';
    document.head.appendChild(css);

    const made = [];
    document.querySelectorAll('select[is="emby-select"]').forEach((sel) => {
        const box = document.createElement('div');
        box.className = 'selectArrowContainer';
        box.style.cssText = 'position:absolute;right:0;top:0;';
        box.innerHTML = '<span class="selectArrow material-icons keyboard_arrow_down"></span>';
        made.push({ box: box, parent: sel.parentNode, after: sel });
    });

    made.forEach((m) => {
        if (mode === 'end') {
            m.parent.appendChild(m.box);
        } else {
            m.parent.insertBefore(m.box, m.after.nextSibling);
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

        const theirs = parent.getBoundingClientRect();
        const mine = arrow ? arrow.getBoundingClientRect() : null;
        const inside = !!mine && mine.left >= theirs.left - 2 && mine.right <= theirs.right + 2 &&
            mine.top >= theirs.top - 2 && mine.top <= theirs.bottom + 2;

        out.push({
            id: sel.id || '(no id)',
            parent: parent.className || '(no class)',
            position: window.getComputedStyle(parent).position,
            ok: held && inside,
            arrow: mine ? [Math.round(mine.left), Math.round(mine.top)] : ['none', 'none']
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
    await p.evaluate(UPGRADE, process.env.STM_ARROW_MODE || 'after');

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
            ' arrow@' + r.arrow.join(','));
    });

    console.log('\n' + (rows.length - failed) + ' of ' + rows.length + ' selects hold their arrow');
    await p.close();
    await browser.close();
    process.exit(failed ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });
