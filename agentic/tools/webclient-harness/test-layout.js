// Layout checks for Web/specialtomovie.js in a real browser engine.
//
// The jsdom harness in test.js covers behaviour, but jsdom does not lay out: getBoundingClientRect
// returns zeroes there, so the two parts of this script that depend on measurement cannot be
// verified by it at all --
//
//   1. matchBadgeMetrics, which sizes the tile from a neighbouring badge
//   2. the aspect-ratio fallback in captionSuppressed, which recognises a badge whose caption is
//      hidden on a child element rather than on the anchor itself
//
// -- and both had to be taken on trust. This drives a real engine so they do not.
//
//   node test-layout.js [path-to-specialtomovie.js]
//
// Uses an already-installed Edge or Chrome through playwright-core, so no browser is downloaded.
// Set STM_BROWSER to point at a specific executable. Exit 0 = passed, 1 = failed, 2 = no browser.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');
const { requireBrowser } = require('./browser');

const scriptArg = process.argv[2] || path.join(__dirname, '..', '..', '..', 'Web', 'specialtomovie.js');
if (!fs.existsSync(scriptArg)) {
    console.error('cannot read the script under test: ' + scriptArg);
    process.exit(2);
}
const SCRIPT = fs.readFileSync(scriptArg, 'utf8');

const browserPath = requireBrowser();

const MOVIE_ID = 'ab12cd34ef567890ab12cd34ef567890';

let pass = 0;
let fail = 0;
function check(name, cond, detail) {
    if (cond) {
        pass++;
        console.log('  PASS  ' + name);
    } else {
        fail++;
        console.log('  FAIL  ' + name + (detail ? '  -> ' + detail : ''));
    }
}
const near = (a, b, tol) => Math.abs(a - b) <= (tol === undefined ? 1 : tol);

// A 1x1 gif, stretched by the width/height below. The logo's real artwork is irrelevant here; only
// the box it occupies matters.
const PIXEL = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

// The reported shape: the converting plugin renders its tiles into a container of its own, and the
// caption is kept for screen readers and hidden on a child span - not on the anchor - so nothing in
// the anchor's own computed style says the words are invisible. Only its proportions do.
function page(badgeHeight) {
    const logo = (w, name) =>
        '<a href="https://example.com/' + name + '" style="display:inline-block;vertical-align:middle;' +
        'margin:0 4px;line-height:0">' +
        '<img src="' + PIXEL + '" style="width:' + w + 'px;height:' + badgeHeight + 'px">' +
        '<span style="position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)">' + name + '</span>' +
        '</a>';

    return '<!doctype html><html><head><style>' +
        'body{background:#101010;color:#ddd;font:14px sans-serif;margin:0;padding:20px}' +
        '.badges{line-height:0}' +
        '</style></head><body>' +
        '<div id="itemDetailPage">' +
        '  <div class="itemExternalLinks">' +
        '    <a href="https://host:8096/web/#/details?id=' + MOVIE_ID + '&serverId=s1&stm=m"' +
        '       target="_blank">Movie Version</a>' +
        '  </div>' +
        '  <div class="badges">' +
        // One tile proportioned like a square-ish logo, one much wider than tall. Only the first can
        // satisfy the aspect-ratio test, which is the point: one match is enough for the row.
        logo(Math.round(badgeHeight * 2), 'aniDB') +
        logo(Math.round(badgeHeight * 3.8), 'MyAnimeList') +
        '  </div>' +
        '</div></body></html>';
}

(async () => {
    console.log('browser: ' + browserPath + '\n');
    const browser = await chromium.launch({ executablePath: browserPath, headless: true });

    try {
        for (const badgeHeight of [32, 24]) {
            console.log('[row with ' + badgeHeight + 'px logos]');
            const p = await browser.newPage();
            await p.setViewportSize({ width: 1200, height: 600 });
            await p.setContent(page(badgeHeight), { waitUntil: 'load' });
            await p.addScriptTag({ content: SCRIPT });
            await p.waitForTimeout(700);

            const r = await p.evaluate(() => {
                const ours = document.querySelector('a[href*="stm="]');
                const badges = document.querySelector('.badges');
                const neighbour = badges.querySelector('a:not([href*="stm="])');
                const box = (el) => {
                    const b = el.getBoundingClientRect();
                    return { top: b.top, height: b.height, width: b.width, centre: b.top + b.height / 2 };
                };
                return {
                    inBadgeRow: ours.parentElement === badges,
                    isLast: badges.lastElementChild === ours,
                    isBadge: ours.classList.contains('specialtomovie-badge'),
                    ours: box(ours),
                    neighbour: box(neighbour),
                    background: getComputedStyle(ours).backgroundImage.slice(0, 30),
                    captionPainted: ours.getBoundingClientRect().width > 0 &&
                        getComputedStyle(ours).textIndent !== '0px' ? false : true,
                    label: ours.getAttribute('aria-label')
                };
            });

            // The aspect-ratio fallback is the only thing that can classify these neighbours, since
            // their captions are hidden on a child span. Reaching badge form at all proves it fired.
            check('badge row detected through the child-hidden caption', r.isBadge,
                'classList did not include specialtomovie-badge');
            check('joined the badge container', r.inBadgeRow);
            check('last in the badge container', r.isLast);
            check('renders the badge artwork', r.background.indexOf('url(') === 0, r.background);
            check('accessible name preserved', r.label === 'Movie Version', 'label=' + r.label);

            // The measurement itself, which is what jsdom could not reach.
            check('height matches the neighbouring logo (' + badgeHeight + 'px)',
                near(r.ours.height, r.neighbour.height),
                'ours=' + r.ours.height + ' neighbour=' + r.neighbour.height);
            check('tile is square', near(r.ours.width, r.ours.height),
                'w=' + r.ours.width + ' h=' + r.ours.height);
            check('vertically centred on the row', near(r.ours.centre, r.neighbour.centre),
                'ours=' + r.ours.centre + ' neighbour=' + r.neighbour.centre);
            check('not the hard-coded 28px box', !near(r.ours.height, 28, 0.5) || badgeHeight === 28,
                'height=' + r.ours.height);

            await p.close();
            console.log('');
        }
    } finally {
        await browser.close();
    }

    console.log('='.repeat(46));
    console.log('pass ' + pass + '   fail ' + fail);
    process.exit(fail === 0 ? 0 : 1);
})().catch((err) => {
    console.error(err);
    process.exit(1);
});
