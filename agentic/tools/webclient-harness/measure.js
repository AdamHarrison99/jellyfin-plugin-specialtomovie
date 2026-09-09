// Counts the style resolutions and forced layouts Web/specialtomovie.js causes per upgrade pass.
//
// The script re-runs on every DOM mutation the web client makes, and a detail page mutates a lot, so
// "how much work does one pass do" is a number worth having rather than guessing at. It exists
// because a guess was wrong once: the upward search for the badge row widens at each hop, and
// re-tested every link the previous hop had already tested. That cost around 320 style resolutions
// per pass on a ten-link row, which reading the code had not made obvious.
//
//   node measure.js [path-to-specialtomovie.js]
//
// Prints counts for a text row and a badge row, initially and across ten further passes. Lower is
// better; there is no pass/fail. Compare two revisions by pointing it at each in turn - a previous
// release can be extracted with `git show <ref>:Web/specialtomovie.js > /tmp/old.js`.
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
const BROWSER = requireBrowser();

const PIXEL = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
const ID = 'ab12cd34ef567890ab12cd34ef567890';
const LINKS = 10;

// Nested a few levels deep, as a real detail page is, so the upward walk has somewhere to go.
function page(kind) {
    let others = '';
    for (let i = 0; i < LINKS; i++) {
        others += kind === 'badges'
            ? '<a href="https://example.com/' + i + '" style="display:inline-block;vertical-align:middle;' +
              'margin:0 4px;line-height:0"><img src="' + PIXEL + '" style="width:64px;height:32px">' +
              '<span style="position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)">' +
              'Logo' + i + '</span></a>'
            : '<a href="https://example.com/' + i + '">Link ' + i + '</a>, ';
    }

    return '<!doctype html><html><head><style>body{background:#101010;color:#ddd}</style></head><body>' +
        '<div id="itemDetailPage"><div class="wrap"><div class="inner">' +
        '<div class="itemExternalLinks">' + others +
        '<a href="https://host:8096/web/#/details?id=' + ID + '&serverId=s1&stm=m"' +
        ' target="_blank">Movie Version</a>' +
        '</div></div></div></div></body></html>';
}

const INSTRUMENT = () => {
    window.__gcs = 0;
    window.__rect = 0;
    const gcs = window.getComputedStyle.bind(window);
    window.getComputedStyle = function (el, pseudo) {
        window.__gcs++;
        return gcs(el, pseudo);
    };
    const rect = Element.prototype.getBoundingClientRect;
    Element.prototype.getBoundingClientRect = function () {
        window.__rect++;
        return rect.call(this);
    };
};

(async () => {
    console.log('browser: ' + BROWSER);
    console.log('script:  ' + path.resolve(scriptArg));
    console.log('row:     ' + LINKS + ' links plus the cross-link\n');

    const browser = await chromium.launch({ executablePath: BROWSER, headless: true });
    const rows = [];

    try {
        for (const kind of ['text', 'badges']) {
            const p = await browser.newPage();
            await p.setContent(page(kind), { waitUntil: 'load' });
            await p.evaluate(INSTRUMENT);
            await p.addScriptTag({ content: SCRIPT });
            await p.waitForTimeout(800);
            const initial = await p.evaluate(() => ({ gcs: window.__gcs, rect: window.__rect }));

            // Ten further mutations of the row, as a re-rendering client would cause.
            await p.evaluate(() => { window.__gcs = 0; window.__rect = 0; });
            for (let i = 0; i < 10; i++) {
                await p.evaluate((n) => {
                    const marker = document.createElement('span');
                    marker.textContent = 'x' + n;
                    document.querySelector('.itemExternalLinks').appendChild(marker);
                }, i);
                await p.waitForTimeout(120);
            }
            const after = await p.evaluate(() => ({ gcs: window.__gcs, rect: window.__rect }));

            rows.push({ kind, initial, after });
            await p.close();
        }
    } finally {
        await browser.close();
    }

    const pad = (n, w) => String(n).padStart(w);
    console.log('row      | initial style  layout | 10 passes: style  layout');
    console.log('---------+-----------------------+-------------------------');
    for (const r of rows) {
        console.log(r.kind.padEnd(8) + ' | ' + pad(r.initial.gcs, 13) + pad(r.initial.rect, 8) +
            ' | ' + pad(r.after.gcs, 17) + pad(r.after.rect, 8));
    }
})().catch((err) => {
    console.error(err);
    process.exit(1);
});
