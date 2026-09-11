// Steady-state cost: what a pass costs once the row has stopped moving.
//
// measure.js appends to the row on every pass, so it forces a re-measure every time and answers
// "what does re-measuring cost". This asks the other question: on a settled row, is the icon
// measured again at all? A correction that re-derives itself must still write nothing when the row
// has not moved, or it can oscillate on a page that is doing nothing.
//
//   node measure-idle.js [path-to-specialtomovie.js]
//
// Prints the settling cost, then the cost of ten passes driven by a class change elsewhere on the
// page. There is no pass or fail -- run it against two revisions and compare. Style-attribute
// writes over the idle passes should be zero.
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

const PIXEL = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
const ID = 'ab12cd34ef567890ab12cd34ef567890';

// A logo row of ten links, under jellyfin-web's own emby-button rules.
function page() {
    let links = '';
    for (let i = 0; i < 10; i++) {
        links += '<a is="emby-linkbutton" class="button-link emby-button logo" href="https://e.com/' + i +
            '">Link ' + i + '</a>, ';
    }

    return '<!doctype html><html><head><style>' +
        'body{background:#101010;color:#ddd;font:16px/1.4 sans-serif}' +
        '.emby-button{display:inline-flex;align-items:center;vertical-align:middle;margin:0.3em;padding:0.9em 1em}' +
        '.button-link{margin:0;padding:0;vertical-align:initial}' +
        '.logo{font-size:0;color:transparent}' +
        '.logo::before{content:"";display:inline-block;width:50px;height:25px;background:url(' + PIXEL +
        ') no-repeat;background-size:contain;margin-right:5px;vertical-align:middle}' +
        '</style></head><body><div id="itemDetailPage"><div class="itemExternalLinks">' + links +
        '<a is="emby-linkbutton" class="button-link emby-button" target="_blank"' +
        ' href="https://host:8096/web/#/details?id=' + ID + '&serverId=s1&stm=m">Linked Movie</a>' +
        '</div><div id="noise">noise</div></div></body></html>';
}

// Counted in the page, before the script is injected, so every call it makes is included.
const INSTRUMENT = () => {
    window.__gcs = 0;
    window.__rect = 0;
    window.__writes = 0;
    const gcs = window.getComputedStyle.bind(window);
    window.getComputedStyle = function (el, pseudo) { window.__gcs++; return gcs(el, pseudo); };
    const rect = Element.prototype.getBoundingClientRect;
    Element.prototype.getBoundingClientRect = function () { window.__rect++; return rect.call(this); };
};

const WATCH = () => {
    const ours = document.querySelector('.itemExternalLinks a[href*="stm="]');
    new MutationObserver((records) => { window.__writes += records.length; })
        .observe(ours, { attributes: true, attributeFilter: ['style'] });
};

(async () => {
    const browser = await chromium.launch({ executablePath: browserPath, headless: true });
    const p = await browser.newPage();

    await p.setViewportSize({ width: 1200, height: 600 });
    await p.setContent(page(), { waitUntil: 'load' });
    await p.evaluate(INSTRUMENT);
    await p.addScriptTag({ content: SCRIPT });

    // Past the last of the script's own settle checks.
    await p.waitForTimeout(4200);
    await p.evaluate(WATCH);

    const settled = await p.evaluate(() => ({ gcs: window.__gcs, rect: window.__rect }));
    await p.evaluate(() => { window.__gcs = 0; window.__rect = 0; window.__writes = 0; });

    // Passes driven by a class change elsewhere on the page: the row itself does not move.
    for (let i = 0; i < 10; i++) {
        await p.evaluate((n) => { document.getElementById('noise').className = 'n' + n; }, i);
        await p.waitForTimeout(150);
    }

    const after = await p.evaluate(() => ({
        gcs: window.__gcs, rect: window.__rect, writes: window.__writes,
        shift: document.querySelector('.itemExternalLinks a[href*="stm="]')
            .style.getPropertyValue('--stm-icon-shift')
    }));

    console.log('settling:        style=' + settled.gcs + ' layout=' + settled.rect);
    console.log('10 idle passes:  style=' + after.gcs + ' layout=' + after.rect +
        '  style-attribute writes=' + after.writes);
    console.log('resting shift:   ' + (after.shift || '(none)'));

    await p.close();
    await browser.close();
})().catch((e) => { console.error(e); process.exit(1); });
