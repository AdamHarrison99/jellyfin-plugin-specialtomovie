// Behavioural checks for Web/specialtomovie.js against a simulated Jellyfin detail page.
//
// The script is progressive enhancement over a row of links the server renders, so almost
// everything that can go wrong with it is a DOM question: does it recognise a row of brand badges,
// does it stay readable on the theme in use, does it end up last, and does a click stay in the app.
// None of that is answerable by reading the file, and all of it has been wrong at least once.
//
//   node test.js ../../../Web/specialtomovie.js
//
// Exit 0 = every check passed, 1 = at least one failed, 2 = bad invocation.
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const scriptArg = process.argv[2] || path.join(__dirname, '..', '..', '..', 'Web', 'specialtomovie.js');
if (!fs.existsSync(scriptArg)) {
    console.error('cannot read the script under test: ' + scriptArg);
    console.error('usage: node test.js [path-to-specialtomovie.js]');
    process.exit(2);
}
const SCRIPT = fs.readFileSync(scriptArg, 'utf8');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Real GUIDs in the "N" form CrossLinkUrlBuilder emits, because the script validates the shape.
const MOVIE_ID = 'ab12cd34ef567890ab12cd34ef567890';
const SPECIAL_ID = '0011223344556677889900aabbccddee';

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

// A detail page row. `badges` decides whether the *other* links render as logo tiles with a caption
// that is present but hidden in CSS — the shape the detector used to get wrong, because it treated
// any anchor carrying text as proof the row was text.
function makePage({ badges, bg }) {
    const others = badges
        ? '<a href="https://anidb.net/a1" style="text-indent:-9999px;overflow:hidden"><img src="x.png">aniDB</a>, ' +
          '<a href="https://imdb.com/t1" style="text-indent:-9999px;overflow:hidden"><img src="y.png">IMDb</a>, '
        : '<a href="https://anidb.net/a1">aniDB</a>, <a href="https://imdb.com/t1">IMDb</a>, ';

    const dom = new JSDOM(
        '<!doctype html><html><body style="background-color:' + bg + '">' +
        '  <div id="itemDetailPage">' +
        '    <div class="itemExternalLinks">' +
        '      ' + others +
        '      <a href="https://host:8096/web/#/details?id=' + MOVIE_ID + '&serverId=s1&stm=m" target="_blank">Movie Version</a>' +
        '    </div>' +
        '  </div>' +
        '</body></html>',
        { runScripts: 'dangerously', pretendToBeVisual: true, url: 'https://host:8096/web/' }
    );

    dom.window.eval(SCRIPT);
    return dom;
}

const ours = (dom) => dom.window.document.querySelector('a[href*="stm="]');
const rowOf = (dom) => dom.window.document.querySelector('.itemExternalLinks');

function clickOn(dom, el, opts) {
    const ev = new dom.window.MouseEvent('click',
        Object.assign({ bubbles: true, cancelable: true, button: 0 }, opts || {}));
    el.dispatchEvent(ev);
    return ev;
}

(async () => {
    // ---- 1. A row of brand badges ----
    console.log('\n[1] Badge row (other links are logo tiles with hidden captions)');
    let dom = makePage({ badges: true, bg: 'rgb(16,16,16)' });
    await sleep(300);
    let a = ours(dom);
    check('upgraded', a.hasAttribute('data-stm-upgraded'));
    check('rendered as a badge', a.classList.contains('specialtomovie-badge'), 'class=' + a.className);
    check('badge left to its own colours', !a.style.color, 'color=' + a.style.color);
    check('caption kept as the accessible name', a.getAttribute('aria-label') === 'Movie Version');
    check('href reduced to a hash',
        a.getAttribute('href') === '#/details?id=' + MOVIE_ID + '&serverId=s1&stm=m', a.getAttribute('href'));
    check('target removed', !a.hasAttribute('target'));
    check('last element in the row', a.parentNode.lastElementChild === a);

    // ---- 2/3. Readability against the actual backdrop, both ways ----
    console.log('\n[2] Text row, dark background');
    dom = makePage({ badges: false, bg: 'rgb(16,16,16)' });
    await sleep(300);
    a = ours(dom);
    check('not a badge', !a.classList.contains('specialtomovie-badge'));
    check('light text on a dark backdrop', a.style.color === 'rgb(233, 233, 233)', 'color=' + a.style.color);
    check('colour set !important', a.style.getPropertyPriority('color') === 'important');
    check('last element in the row', a.parentNode.lastElementChild === a);

    console.log('\n[3] Text row, light background');
    dom = makePage({ badges: false, bg: 'rgb(245,245,245)' });
    await sleep(300);
    check('dark text on a light backdrop', ours(dom).style.color === 'rgb(28, 28, 28)', ours(dom).style.color);

    // ---- 4. The reported bug: the first click, before the upgrade pass has run ----
    console.log('\n[4] Click on a not-yet-upgraded anchor (the new-tab bug)');
    dom = makePage({ badges: true, bg: 'rgb(16,16,16)' });
    // Deliberately no wait. Rebuild the anchor the way the web client does on a re-render: absolute
    // URL, target="_blank", no upgrade marker. This is the window the first click used to land in.
    let row = rowOf(dom);
    row.innerHTML = '<a href="https://host:8096/web/#/details?id=' + SPECIAL_ID + '&serverId=s1&stm=s" target="_blank">TV Special</a>';
    const fresh = row.querySelector('a');
    check('anchor really is unupgraded', !fresh.hasAttribute('data-stm-upgraded'));
    check('anchor still targets a new tab', fresh.getAttribute('target') === '_blank');

    const ev = clickOn(dom, fresh);
    check('click intercepted anyway (no new tab)', ev.defaultPrevented);
    await sleep(50);
    check('navigated in-app to the linked item',
        decodeURIComponent(dom.window.location.hash) === '#/details?id=' + SPECIAL_ID + '&serverId=s1&stm=s',
        dom.window.location.hash);

    // ---- 5. A deliberate new tab is still the user's to ask for ----
    console.log('\n[5] Ctrl-click still opens a new tab');
    check('ctrl-click not intercepted',
        !clickOn(dom, rowOf(dom).querySelector('a'), { ctrlKey: true }).defaultPrevented);

    // ---- 6/7. Staying last, and not exhausting the move budget doing it ----
    console.log('\n[6] Re-appended to the end after another plugin appends');
    dom = makePage({ badges: true, bg: 'rgb(16,16,16)' });
    await sleep(300);
    row = rowOf(dom);
    const intruder = dom.window.document.createElement('a');
    intruder.href = 'https://trakt.tv/x';
    intruder.textContent = 'Trakt';
    row.appendChild(intruder);
    check('intruder is last before our pass', row.lastElementChild === intruder);
    await sleep(300);
    check('we are last again', row.lastElementChild === ours(dom), 'last=' + row.lastElementChild.textContent);

    console.log('\n[7] Move budget released once the row settles');
    check('move counter cleared after settling', !ours(dom).hasAttribute('data-stm-moves'),
        'moves=' + ours(dom).getAttribute('data-stm-moves'));
    for (let i = 0; i < 12; i++) {
        const x = dom.window.document.createElement('a');
        x.href = 'https://example.com/' + i;
        x.textContent = 'X' + i;
        row.appendChild(x);
        await sleep(160);
    }
    check('still last after 12 rounds of being displaced', row.lastElementChild === ours(dom),
        'last=' + row.lastElementChild.textContent);

    // ---- 8. Links that are not ours are left entirely alone ----
    // The click handler sees every click in the document and cancels the default on anything
    // parseLink accepts, so this is the check that keeps it from swallowing other plugins' links.
    console.log('\n[8] Foreign links untouched');
    dom = makePage({ badges: false, bg: 'rgb(16,16,16)' });
    row = rowOf(dom);
    const foreigners = {
        'query text only': 'https://example.com/?x=stm=1',
        'right params, wrong route': 'https://example.com/#/other?id=' + MOVIE_ID + '&stm=m',
        'right route, non-GUID id': 'https://example.com/#/details?id=notaguid&stm=m',
        'right route, unknown marker': 'https://example.com/#/details?id=' + MOVIE_ID + '&stm=zz'
    };
    const made = {};
    Object.keys(foreigners).forEach((name) => {
        const el = dom.window.document.createElement('a');
        el.setAttribute('href', foreigners[name]);
        el.textContent = name;
        row.appendChild(el);
        made[name] = el;
    });
    await sleep(300);
    Object.keys(foreigners).forEach((name) => {
        check('not upgraded: ' + name, !made[name].hasAttribute('data-stm-upgraded'));
        check('click not intercepted: ' + name, !clickOn(dom, made[name]).defaultPrevented);
    });

    // ---- 9. A second copy of the script must be inert ----
    console.log('\n[9] Second load is inert');
    dom = makePage({ badges: false, bg: 'rgb(16,16,16)' });
    dom.window.eval(SCRIPT);
    await sleep(300);
    row = rowOf(dom);
    let doubled = 0;
    row.childNodes.forEach((n) => {
        if (n.nodeType === 3 && /^,\s*,/.test(n.nodeValue.trim())) {
            doubled++;
        }
    });
    check('no doubled separators', doubled === 0, 'doubled=' + doubled);
    check('single cross-link still present', row.querySelectorAll('a[href*="stm="]').length === 1);

    // ---- 10. The badges live in a container of their own ----
    // The reported shape: a plugin converts the text links into tiles and puts them somewhere else,
    // leaving the cross-link behind in the original container. Looking only at the link's own parent
    // saw a row of one and called it text, which is why one side of the pair rendered as a badge and
    // the other stayed as words.
    console.log('\n[10] Badges in a separate container');
    dom = new JSDOM(
        '<!doctype html><html><body style="background-color:rgb(16,16,16)">' +
        '  <div id="itemDetailPage">' +
        '    <div class="itemExternalLinks">' +
        '      <a href="https://host:8096/web/#/details?id=' + MOVIE_ID + '&serverId=s1&stm=m" target="_blank">Movie Version</a>' +
        '    </div>' +
        '    <div class="converted-badges">' +
        '      <a href="https://anidb.net/a1" style="display:inline-block;vertical-align:middle;margin:0px 4px;text-indent:-9999px;overflow:hidden"><img src="x.png">aniDB</a>' +
        '      <a href="https://imdb.com/t1" style="display:inline-block;vertical-align:middle;margin:0px 4px;text-indent:-9999px;overflow:hidden"><img src="y.png">IMDb</a>' +
        '    </div>' +
        '  </div>' +
        '</body></html>',
        { runScripts: 'dangerously', pretendToBeVisual: true, url: 'https://host:8096/web/' }
    );
    dom.window.eval(SCRIPT);
    await sleep(300);

    a = ours(dom);
    const badgeRow = dom.window.document.querySelector('.converted-badges');
    check('found the badge row it was not inside', a.classList.contains('specialtomovie-badge'),
        'class=' + a.className);
    check('moved into the badge container', a.parentNode === badgeRow,
        'parent=' + a.parentNode.className);
    check('last in the badge container', badgeRow.lastElementChild === a);
    check('no leftover text colour', !a.style.color, 'color=' + a.style.color);

    // ---- 11. Alignment is taken from a real neighbour, not hard-coded ----
    console.log('\n[11] Metrics copied from a neighbouring badge');
    check('display matched', a.style.display === 'inline-block', 'display=' + a.style.display);
    check('vertical-align matched', a.style.verticalAlign === 'middle', 'va=' + a.style.verticalAlign);
    check('left margin matched', a.style.marginLeft === '4px', 'ml=' + a.style.marginLeft);
    check('right margin matched', a.style.marginRight === '4px', 'mr=' + a.style.marginRight);
    // jsdom does not lay out, so getBoundingClientRect is all zeroes and the height/width copy is
    // skipped rather than applied wrongly. Sizing needs a real browser; this only pins the rest.
    check('no bogus zero-height box from a non-laying-out DOM',
        a.style.height !== '0px' && a.style.width !== '0px',
        'h=' + a.style.height + ' w=' + a.style.width);

    console.log('\n' + '='.repeat(46));
    console.log('pass ' + pass + '   fail ' + fail);
    process.exit(fail === 0 ? 0 : 1);
})();
