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
//   node test-layout.js [path-to-specialtomovie.js] [path-to-script-pasted-later]
//
// A second path models a console script pasted into a page that is already running: it is injected
// after the row has settled, and the checkpoints before that are reported rather than asserted.
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

const lateArg = process.argv[3];
if (lateArg && !fs.existsSync(lateArg)) {
    console.error('cannot read the script pasted later: ' + lateArg);
    process.exit(2);
}
const LATE = lateArg ? fs.readFileSync(lateArg, 'utf8') : null;
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
        '&serverId=s1&stm=m" target="_blank">Linked Movie</a>';
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
        '&serverId=s1&stm=m">Linked Movie</a>' +
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

// ---------------------------------------------------------------------------------------------
// The row as the web client really builds it, and as it really settles.
//
// Every link here is an `is="emby-linkbutton"` anchor, and jellyfin-web's own CSS gives the
// `emby-button` class `display: inline-flex; vertical-align: middle` while `button-link` zeroes the
// padding. That class is added when the element is upgraded, which happens after the row is first
// rendered; the logo CSS a user installs can apply later still. Until both have landed a link is a
// plain inline box sitting on the baseline, and its visual centre is several pixels from where it
// ends up. A measurement taken in that window describes a row that no longer exists, which is what
// these scenarios reproduce -- the icon was measured once and never again.
const REAL_SITES = [
    { host: 'imdb.com', url: 'https://imdb.com/title/tt1', name: 'IMDb', wide: 2 },
    { host: 'thetvdb.com', url: 'https://thetvdb.com/series/s1', name: 'TheTVDB', wide: 3 },
    { host: 'themoviedb.org', url: 'https://themoviedb.org/tv/1', name: 'TheMovieDb', wide: 2 },
    { host: 'trakt.tv', url: 'https://trakt.tv/shows/1', name: 'Trakt', wide: 1 }
];

const logoRules = (scope) => REAL_SITES.map((s) =>
    scope + ' a[href*="' + s.host + '"] {' +
    '  background: none !important; color: transparent !important;' +
    '  padding: 0 !important; font-size: 0;' +
    '}' +
    scope + ' a[href*="' + s.host + '"]::before {' +
    '  content: ""; display: inline-block;' +
    '  width: ' + (25 * s.wide) + 'px; height: 25px;' +
    '  background-image: url(' + PIXEL + '); background-size: contain;' +
    '  background-repeat: no-repeat; margin-right: 5px; vertical-align: middle;' +
    '}').join('');

function realPage({ hiddenBefore, upgraded }) {
    const link = (href, text, extra) =>
        '<a is="emby-linkbutton" class="button-link' + (upgraded ? ' emby-button' : '') +
        (extra || '') + '" href="' + href + '">' + text + '</a>';

    const links = REAL_SITES.map((s) => link(s.url, s.name));

    // A provider the installed logo CSS has no rule for, hidden by a theme. It has no position, so
    // it cannot say where the row sits -- and it is the sibling directly before the icon.
    if (hiddenBefore) {
        links.push(link('https://example.com/collection', 'Collection', ' nowhere'));
    }

    links.push('<a is="emby-linkbutton" class="button-link' + (upgraded ? ' emby-button' : '') +
        '" target="_blank" href="https://host:8096/web/#/details?id=' + MOVIE_ID +
        '&serverId=s1&stm=s">Linked Special</a>');

    return '<!doctype html><html><head><style>' +
        'body { background: #101010; color: #ddd; font: 16px/1.4 sans-serif; margin: 0; padding: 16px }' +
        '.hide { display: none }' +
        '.itemExternalLinks a { text-decoration: none }' +
        // jellyfin-web's emby-button.scss, in the parts that place the box.
        '.emby-button { position: relative; display: inline-flex; align-items: center;' +
        '  box-sizing: border-box; margin: 0.3em; padding: 0.9em 1em; font-size: inherit;' +
        '  vertical-align: middle; }' +
        '.button-link { background: transparent; margin: 0; padding: 0; vertical-align: initial; }' +
        logoRules('.logos') +
        // Last and forced, so the upgrade cannot bring this link back.
        '.nowhere { display: none !important }' +
        '</style></head><body>' +
        '<div id="itemDetailPage">' +
        // Joined with ', ', the way renderLinks does it.
        '  <div class="itemExternalLinks">' + links.join(', ') + '</div>' +
        '</div>' +
        '</body></html>';
}

// The two steps that change every link's shape: the element upgrade and the logo CSS.
const SETTLE = () => {
    const row = document.querySelector('#itemDetailPage:not(.hide) .itemExternalLinks');
    row.classList.add('logos');
    [].slice.call(row.querySelectorAll('a')).forEach((a) => a.classList.add('emby-button'));
};

// Alignment as painted, against the last element before the icon that the row actually draws.
const ALIGN = () => {
    const row = document.querySelector('#itemDetailPage:not(.hide) .itemExternalLinks');
    const ours = row.querySelector('a[href*="stm="]');
    const drawn = [].slice.call(row.children)
        .filter((el) => el !== ours && el.getBoundingClientRect().width > 0);
    const reference = drawn.length ? drawn[drawn.length - 1] : null;

    const painted = (el) => {
        const b = el.getBoundingClientRect();
        for (const dx of [1, 3, 6, 10, 16, 24, 40, 60]) {
            const x = b.left + dx;
            let top = null;
            let bottom = null;
            for (let y = 0; y < 600; y += 0.5) {
                const hit = document.elementFromPoint(x, y);
                if (hit && el.contains(hit)) { if (top === null) { top = y; } bottom = y; }
            }
            if (top !== null) { return { top: top, bottom: bottom, centre: (top + bottom) / 2 }; }
        }
        return null;
    };

    const gaps = [];
    for (let i = 1; i < drawn.length; i++) {
        const a = drawn[i - 1].getBoundingClientRect();
        const b = drawn[i].getBoundingClientRect();
        if (Math.abs(b.top - a.top) < 2 && b.left >= a.right - 0.5) { gaps.push(b.left - a.right); }
    }
    gaps.sort((x, y) => x - y);

    return {
        shiftVar: ours.style.getPropertyValue('--stm-icon-shift'),
        gapVar: ours.style.getPropertyValue('--stm-icon-gap'),
        ours: painted(ours),
        reference: reference ? painted(reference) : null,
        referenceName: reference ? reference.textContent : null,
        gapBeforeUs: reference
            ? ours.getBoundingClientRect().left - reference.getBoundingClientRect().right
            : null,
        rowMedianGap: gaps.length ? gaps[Math.floor(gaps.length / 2)] : null
    };
};

// The correction the icon is carrying, against the one this row asks for now. Zero means the icon
// is placed for the row as it stands; anything else is a measurement the row has outgrown, and it
// is the number of pixels the icon is out by.
const DRIFT = () => {
    const row = document.querySelector('#itemDetailPage:not(.hide) .itemExternalLinks');
    const ours = row.querySelector('a[href*="stm="]');
    const drawn = [].slice.call(row.children)
        .filter((el) => el !== ours && el.getBoundingClientRect().width > 0);
    const reference = drawn.length ? drawn[drawn.length - 1] : null;
    if (!reference) { return { drift: null }; }

    const centre = (el) => {
        const r = el.getBoundingClientRect();
        return r.height > 0 ? r.top + r.height / 2 : r.top;
    };
    const applied = parseFloat(ours.style.getPropertyValue('--stm-icon-shift')) || 0;

    return {
        drift: centre(reference) - centre(ours) - applied,
        shiftVar: ours.style.getPropertyValue('--stm-icon-shift'),
        referenceName: reference.textContent
    };
};

function describes(name, state) {
    check(name, state.drift !== null && Math.abs(state.drift) <= 0.5,
        state.drift === null
            ? 'nothing drawn before the icon'
            : 'out by ' + Math.round(state.drift * 100) / 100 + 'px against ' + state.referenceName +
              ', shiftVar=' + state.shiftVar);
}

function centred(name, state) {
    check(name,
        !!(state.ours && state.reference) && near(state.ours.centre, state.reference.centre),
        state.ours && state.reference
            ? 'ours=' + state.ours.centre + ' ' + state.referenceName + '=' + state.reference.centre +
              ' shiftVar=' + state.shiftVar
            : 'could not hit-test the painted icon');
}

// A console script is pasted into a page that is already running, so it arrives after the row has
// settled. With no second path given, every scenario runs exactly as it did before.
async function pasteLate(p) {
    const state = await p.evaluate(DRIFT);
    console.log('  (before pasting: out by ' + Math.round(state.drift * 100) / 100 + 'px)');
    await p.addScriptTag({ content: LATE });
    await p.waitForTimeout(900);
}

async function settlingScenario(browser, title, opts) {
    console.log('[' + title + ']');
    const p = await browser.newPage();
    await p.setViewportSize({ width: 1200, height: 600 });
    await p.setContent(realPage(opts), { waitUntil: 'load' });
    await p.addScriptTag({ content: SCRIPT });
    await p.waitForTimeout(800);

    if (!LATE) { describes('correction fits the row as first rendered', await p.evaluate(DRIFT)); }

    await p.evaluate(SETTLE);
    await p.waitForTimeout(900);
    if (LATE) { await pasteLate(p); }
    const after = await p.evaluate(ALIGN);

    describes('correction fits the settled row', await p.evaluate(DRIFT));
    centred('painted on the settled row', after);

    const spacing = 'ours=' + Math.round(after.gapBeforeUs * 10) / 10 +
        ' row=' + Math.round(after.rowMedianGap * 10) / 10 + ' gapVar=' + after.gapVar;

    if (opts.hiddenBefore) {
        // The hidden link's own ", " separators stay in the row, and the row's text is not ours to
        // edit, so the icon can only ever be pushed further out -- never pulled closer.
        check('not spaced tighter than the settled row',
            after.rowMedianGap !== null && after.gapBeforeUs !== null &&
                after.gapBeforeUs >= after.rowMedianGap - 2, spacing);
    } else {
        check('spaced like the settled row',
            after.rowMedianGap !== null && after.gapBeforeUs !== null &&
                near(after.gapBeforeUs, after.rowMedianGap, 2), spacing);
    }

    await p.close();
    console.log('');
}

// A narrow viewport puts the icon on a line of its own. The line below is centred by its own
// baseline, so a correction measured against the line above drags the icon up into it.
async function wrapScenario(browser) {
    console.log('[narrow row, then widened]');
    const p = await browser.newPage();
    await p.setViewportSize({ width: 300, height: 600 });
    await p.setContent(realPage({}), { waitUntil: 'load' });
    await p.addScriptTag({ content: SCRIPT });
    await p.evaluate(SETTLE);
    await p.waitForTimeout(900);
    if (LATE) { await pasteLate(p); }

    const wrapped = await p.evaluate(ALIGN);
    check('wrapped icon left on its own line',
        !!(wrapped.ours && wrapped.reference) && wrapped.ours.top > wrapped.reference.bottom,
        wrapped.ours && wrapped.reference
            ? 'ours=' + wrapped.ours.top + ' reference bottom=' + wrapped.reference.bottom +
              ' shiftVar=' + wrapped.shiftVar
            : 'could not hit-test the painted icon');

    await p.setViewportSize({ width: 1200, height: 600 });
    await p.waitForTimeout(900);
    describes('correction fits the widened row', await p.evaluate(DRIFT));
    centred('centred again after the row is widened', await p.evaluate(ALIGN));

    await p.close();
    console.log('');
}

// The hardest version of the same fault: the logo CSS a user installs arrives after the row was
// measured, restyling every link in it without a single DOM change for an observer to see.
async function lateStylesheetScenario(browser) {
    console.log('[logo CSS arriving late, with no DOM change]');
    const p = await browser.newPage();
    await p.setViewportSize({ width: 1200, height: 600 });
    await p.setContent(realPage({ upgraded: true }), { waitUntil: 'load' });
    await p.addScriptTag({ content: SCRIPT });
    await p.waitForTimeout(700);

    if (!LATE) { describes('correction fits the row as first styled', await p.evaluate(DRIFT)); }

    // Into the head, so nothing under body changes.
    await p.addStyleTag({ content: logoRules('.itemExternalLinks') });
    await p.waitForTimeout(900);
    if (LATE) { await pasteLate(p); }
    await p.waitForTimeout(2700);

    describes('correction fits the row the stylesheet made', await p.evaluate(DRIFT));
    centred('painted on the restyled row', await p.evaluate(ALIGN));

    await p.close();
    console.log('');
}

async function scenario(browser, title, opts, expectedIcon) {
    console.log('[' + title + ']');
    const p = await browser.newPage();
    await p.setViewportSize({ width: 1200, height: 600 });
    await p.setContent(page(opts), { waitUntil: 'load' });
    await p.addScriptTag({ content: SCRIPT });
    await p.waitForTimeout(800);
    if (LATE) {
        await p.addScriptTag({ content: LATE });
        await p.waitForTimeout(900);
    }

    const r = await p.evaluate(MEASURE);

    check('rendered as an icon', r.hasClass);
    check('icon artwork painted', r.iconPainted);
    check('last in the row', r.isLast);
    check('accessible name preserved', r.label === 'Linked Movie', 'label=' + r.label);
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

        // The row the web client really renders, measured while it is still settling.
        await settlingScenario(browser, 'row upgraded after the icon was placed', {});

        // The sibling directly before the icon is hidden, so it has no position to sit against.
        await settlingScenario(browser, 'hidden link directly before the icon',
            { hiddenBefore: true });

        await wrapScenario(browser);
        await lateStylesheetScenario(browser);
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
