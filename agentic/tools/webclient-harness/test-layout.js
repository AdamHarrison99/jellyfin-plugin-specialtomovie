// Layout checks for Web/specialtomovie.js in a real browser engine.
//
// test.js covers behaviour, but jsdom does not lay out: getBoundingClientRect returns zeroes there,
// so the two parts of this script that depend on measurement cannot be verified by it at all --
//
//   1. iconSize, which sizes the icon from the content height of a link the web client rendered
//   2. matchRowSpacing, which leaves the same gap before the icon as the row leaves between its own
//      links, by measuring that gap rather than assuming where the theme keeps it
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
const near = (a, b, tol) => Math.abs(a - b) <= (tol === undefined ? 1.5 : tol);

// A 1x1 gif, stretched by the width/height in the rule. The artwork is irrelevant here; only the box
// it occupies matters.
const PIXEL = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

const SITES = [
    { host: 'anidb.net', url: 'https://anidb.net/a1', name: 'aniDB', wide: 4 },
    { host: 'imdb.com', url: 'https://imdb.com/t1', name: 'IMDb', wide: 2 },
    { host: 'thetvdb.com', url: 'https://thetvdb.com/s1', name: 'TheTVDB', wide: 3 }
];

// The real shape, taken from Druidblack/jellyfin-icon-metadata: each provider's link is neutralised
// and given a logo in a ::before, keyed on the href so the rules match that provider and nothing
// else. Widths differ per logo, exactly as wordmarks do, so anything that assumed a square neighbour
// would be caught here.
function page({ logos, logoHeight, ourPosition }) {
    const rules = logos
        ? SITES.map((s) =>
            '.itemExternalLinks a[href*="' + s.host + '"] {' +
            '  background: none !important; color: transparent !important;' +
            '  padding: 0 !important; font-size: 0;' +
            '}' +
            '.itemExternalLinks a[href*="' + s.host + '"]::before {' +
            '  content: ""; display: inline-block;' +
            '  width: ' + (logoHeight * s.wide) + 'px; height: ' + logoHeight + 'px;' +
            '  background-image: url(' + PIXEL + '); background-size: contain;' +
            '  background-repeat: no-repeat; margin-right: 5px; vertical-align: middle;' +
            '}').join('')
        : '';

    const ourLink = '<a href="https://host:8096/web/#/details?id=' + MOVIE_ID +
        '&serverId=s1&stm=m" target="_blank">Movie Version</a>';
    const siteLinks = SITES.map((s) => '<a href="' + s.url + '">' + s.name + '</a>');

    // Dropped in before the last provider when the move path is being exercised.
    const inRow = ourPosition === 'middle'
        ? [siteLinks[0], siteLinks[1], ourLink, siteLinks[2]]
        : siteLinks.concat([ourLink]);

    return '<!doctype html><html><head><style>' +
        'body { background:#101010; color:#ddd; font:13.6px/1.4 sans-serif; margin:0; padding:20px }' +
        '.hide { display: none }' +
        '.itemExternalLinks a { text-decoration: none }' +
        rules +
        '</style></head><body>' +
        '<div id="itemDetailPage">' +
        '  <div class="itemExternalLinks">\n    ' + inRow.join('\n    ') + '\n  </div>' +
        '</div>' +
        '<div id="itemDetailPage2" class="hide">' +
        '  <div class="itemExternalLinks">' +
        '    <a href="https://imdb.com/t9">IMDb</a>' +
        '    <a href="https://host:8096/web/#/details?id=' + MOVIE_ID +
        '&serverId=s1&stm=m">Movie Version</a>' +
        '  </div>' +
        '</div>' +
        '</body></html>';
}

const MEASURE = () => {
    const visible = document.querySelector('#itemDetailPage:not(.hide)');
    const row = visible.querySelector('.itemExternalLinks');
    const ours = row.querySelector('a[href*="stm="]');
    const others = [].slice.call(row.querySelectorAll('a')).filter((a) => a !== ours);

    const box = (el) => {
        const b = el.getBoundingClientRect();
        return { top: b.top, left: b.left, right: b.right, height: b.height, width: b.width,
                 centre: b.top + b.height / 2 };
    };

    // Where an element is actually painted, found by hit-testing rather than by reading a rect.
    //
    // Neither kind of link in this row can be measured from its own box. A link showing a logo is
    // an inline anchor with font-size 0, so its rect collapses to zero height on the baseline; and
    // the icon's vertical correction is a relative offset on the ::before, which moves paint
    // without moving layout. elementFromPoint sees what the user sees, and it is independent of
    // the arithmetic the script uses to place the icon - which is the point of checking it here.
    const painted = (el) => {
        const b = el.getBoundingClientRect();
        const columns = [1, 3, 6, 10, 16, 24, 40, 60];
        for (const dx of columns) {
            const x = b.left + dx;
            let top = null;
            let bottom = null;
            for (let y = 0; y < 400; y += 0.5) {
                if (document.elementFromPoint(x, y) === el) {
                    if (top === null) { top = y; }
                    bottom = y;
                }
            }
            if (top !== null) {
                return { top: top, bottom: bottom, centre: (top + bottom) / 2 };
            }
        }
        return null;
    };

    const gaps = [];
    for (let i = 1; i < others.length; i++) {
        const a = others[i - 1].getBoundingClientRect();
        const b = others[i].getBoundingClientRect();
        if (Math.abs(b.top - a.top) < 2 && b.left >= a.right - 0.5) { gaps.push(b.left - a.right); }
    }
    gaps.sort((x, y) => x - y);

    const prev = ours.previousElementSibling;
    const before = window.getComputedStyle(ours, '::before');
    const inline = [];
    for (let i = 0; i < ours.style.length; i++) { inline.push(ours.style[i]); }

    return {
        isLast: row.lastElementChild === ours,
        hasClass: ours.classList.contains('specialtomovie-link'),
        iconPainted: before.backgroundImage.indexOf('url(') === 0 &&
            before.backgroundImage.indexOf('svg') !== -1,
        iconWidth: parseFloat(before.width),
        iconHeight: parseFloat(before.height),
        sizeVar: ours.style.getPropertyValue('--stm-icon-size'),
        gapVar: ours.style.getPropertyValue('--stm-icon-gap'),
        shiftVar: ours.style.getPropertyValue('--stm-icon-shift'),
        inlineProperties: inline,
        ours: box(ours),
        prev: prev ? box(prev) : null,
        oursPainted: painted(ours),
        prevPainted: prev ? painted(prev) : null,
        rowMedianGap: gaps.length ? gaps[Math.floor(gaps.length / 2)] : null,
        gapBeforeUs: prev ? ours.getBoundingClientRect().left - prev.getBoundingClientRect().right : null,
        label: ours.getAttribute('aria-label'),
        href: ours.getAttribute('href'),
        hasTarget: ours.hasAttribute('target'),
        hiddenCopyStyled: !!document.querySelector('#itemDetailPage2 a.specialtomovie-link')
    };
};

const ALLOWED_INLINE = ['--stm-icon-size', '--stm-icon-gap', '--stm-icon-shift'];

async function scenario(browser, title, opts, expectedIcon) {
    console.log('[' + title + ']');
    const p = await browser.newPage();
    await p.setViewportSize({ width: 1200, height: 600 });
    await p.setContent(page(opts), { waitUntil: 'load' });
    await p.addScriptTag({ content: SCRIPT });
    await p.waitForTimeout(800);

    const r = await p.evaluate(MEASURE);

    check('rendered as an icon', r.hasClass);
    check('icon artwork painted', r.iconPainted);
    check('last in the row', r.isLast);
    check('accessible name preserved', r.label === 'Movie Version', 'label=' + r.label);
    check('href reduced to a hash', (r.href || '').charAt(0) === '#', r.href);
    check('target removed', !r.hasTarget);

    // The measurement jsdom cannot reach.
    check('icon sized from the row (' + expectedIcon + 'px)',
        near(r.iconHeight, expectedIcon) && near(r.iconWidth, expectedIcon),
        'icon=' + r.iconWidth + 'x' + r.iconHeight + ' var=' + r.sizeVar);
    check('icon is square', near(r.iconWidth, r.iconHeight),
        'w=' + r.iconWidth + ' h=' + r.iconHeight);
    check('vertically centred on the row',
        (r.oursPainted && r.prevPainted)
            ? near(r.oursPainted.centre, r.prevPainted.centre)
            : false,
        (r.oursPainted && r.prevPainted)
            ? 'ours=' + r.oursPainted.centre + ' prev=' + r.prevPainted.centre +
              ' shiftVar=' + r.shiftVar
            : 'could not hit-test the painted icon');

    check('icon painted at the size it was given',
        r.oursPainted ? near(r.oursPainted.bottom - r.oursPainted.top, expectedIcon, 2) : false,
        r.oursPainted
            ? 'painted=' + (r.oursPainted.bottom - r.oursPainted.top) + ' expected=' + expectedIcon
            : 'no painted box');

    // The spacing correction. Matching the row is the requirement; whether a correction was needed
    // to get there depends on the markup, so the gap is what is asserted, not the adjustment.
    if (r.rowMedianGap !== null && r.gapBeforeUs !== null) {
        check('spaced like the rest of the row',
            near(r.gapBeforeUs, r.rowMedianGap, 2),
            'ours=' + Math.round(r.gapBeforeUs * 10) / 10 +
            ' row=' + Math.round(r.rowMedianGap * 10) / 10 + ' gapVar=' + r.gapVar);
    } else {
        check('spaced like the rest of the row', false, 'could not measure the row');
    }

    // The regression guard: the row lays this link out, so nothing may be written over it.
    const stray = r.inlineProperties.filter((n) => ALLOWED_INLINE.indexOf(n) === -1);
    check('no geometry written to the anchor', stray.length === 0,
        'stray=' + stray.join(',') + ' all=' + r.inlineProperties.join(','));

    check('copy on the hidden page left alone', !r.hiddenCopyStyled);

    await p.close();
    console.log('');
}

(async () => {
    console.log('browser: ' + browserPath + '\n');
    const browser = await chromium.launch({ executablePath: browserPath, headless: true });

    try {
        // A row of logos at the size jellyfin-icon-metadata ships, with the link already last.
        await scenario(browser, 'logo row, 25px logos', { logos: true, logoHeight: 25 }, 25);

        // A theme that draws them larger. A fixed 25px box would pass the first scenario and fail
        // this one, which is the whole reason the size is measured.
        await scenario(browser, 'logo row, 36px logos', { logos: true, logoHeight: 36 }, 36);

        // The link starts in the middle, so moveToEnd runs and has to put a separator back. Without
        // that the icon butts against the previous logo with no space at all.
        await scenario(browser, 'logo row, link starts mid-row',
            { logos: true, logoHeight: 25, ourPosition: 'middle' }, 25);

        // No logo CSS at all: plain text links, as on a stock install. The icon sizes to the text
        // link's content height rather than to a logo.
        const textHeight = await (async () => {
            const p = await browser.newPage();
            await p.setContent(page({ logos: false }), { waitUntil: 'load' });
            const h = await p.evaluate(() => {
                const a = document.querySelector('.itemExternalLinks a');
                const cs = getComputedStyle(a);
                const chrome = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) +
                    parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
                return a.getBoundingClientRect().height - chrome;
            });
            await p.close();
            return h;
        })();
        await scenario(browser, 'text row (no icon CSS installed)', { logos: false },
            Math.round(textHeight * 10) / 10);
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
