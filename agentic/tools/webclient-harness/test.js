// Behavioural checks for Web/specialtomovie.js against a simulated Jellyfin detail page.
//
// The script is progressive enhancement over a row of links the server renders, so almost
// everything that can go wrong with it is a DOM question: does it render as an icon, does it end up
// last, is it left alone on a page nobody is looking at, and does a click stay in the app. None of
// that is answerable by reading the file, and all of it has been wrong at least once.
//
//   node test.js ../../../Web/specialtomovie.js
//
// Sizing and spacing are not checked here. Both are measurements, jsdom does not lay out, and the
// harness that does own them is test-layout.js. What this file pins is that no geometry is written
// to the anchor at all - see [11], which is the regression guard for three releases' worth of bugs
// caused by doing exactly that.
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

// The properties the script is allowed to write to the anchor. Anything else appearing inline is a
// return of the approach that broke this three times over: sizing the link by copying a neighbour's
// geometry, which fought the theme, the row and the other plugins in it.
const ALLOWED_INLINE = ['--stm-icon-size', '--stm-icon-gap', '--stm-icon-shift'];

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

// A detail page row. `logos` decides whether the *other* links render as logo tiles with a caption
// that is present but hidden in CSS, the way jellyfin-icon-metadata renders them. The outcome must
// now be identical either way: the script no longer tries to work out which kind of row it is in,
// which is what removed this entire class of bug.
function makePage({ logos }) {
    const others = logos
        ? '<a href="https://anidb.net/a1" style="font-size:0"><span>aniDB</span></a> ' +
          '<a href="https://imdb.com/t1" style="font-size:0"><span>IMDb</span></a> '
        : '<a href="https://anidb.net/a1">aniDB</a>, <a href="https://imdb.com/t1">IMDb</a>, ';

    const dom = new JSDOM(
        '<!doctype html><html><body>' +
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

// Inline properties actually set on the element, custom properties included.
function inlineProperties(el) {
    const style = el.style;
    const names = [];
    for (let i = 0; i < style.length; i++) {
        names.push(style[i]);
    }
    return names;
}

function clickOn(dom, el, opts) {
    const ev = new dom.window.MouseEvent('click',
        Object.assign({ bubbles: true, cancelable: true, button: 0 }, opts || {}));
    el.dispatchEvent(ev);
    return ev;
}

(async () => {
    // ---- 1/2. The same result whatever the rest of the row looks like ----
    // Previously the script decided between an icon and a text link by inspecting its neighbours,
    // and got it wrong in both directions: it read a row of logos as text and left the link as
    // words on one side of a pair while rendering it correctly on the other. It no longer asks.
    for (const logos of [false, true]) {
        console.log('\n[' + (logos ? 2 : 1) + '] ' + (logos ? 'Logo row' : 'Text row'));
        const dom = makePage({ logos });
        await sleep(300);
        const a = ours(dom);

        check('upgraded', a.hasAttribute('data-stm-upgraded'));
        check('rendered as an icon', a.classList.contains('specialtomovie-link'), 'class=' + a.className);
        check('caption kept as the accessible name', a.getAttribute('aria-label') === 'Movie Version');
        check('caption kept as the tooltip', a.getAttribute('title') === 'Movie Version');
        check('caption still in the DOM for screen readers',
            (a.textContent || '').trim() === 'Movie Version');
        check('href reduced to a hash',
            a.getAttribute('href') === '#/details?id=' + MOVIE_ID + '&serverId=s1&stm=m',
            a.getAttribute('href'));
        check('target removed', !a.hasAttribute('target'));
        check('last element in the row', a.parentNode.lastElementChild === a);
    }

    // ---- 3. Copies on a page the user is not looking at ----
    // The web client keeps several detail pages in the document and hides all but one, so the same
    // server-rendered link exists more than once. Styling every copy put an icon on a hidden page
    // and left the visible one wrong - reported as two icons on one item and none on the other.
    //
    // Pages are hidden by toggling a class, which is what the script's observer watches, so that is
    // how they are hidden here. An earlier version of this check set the style attribute instead and
    // failed for a reason that says nothing about the script: a style change is not observed, and
    // adding it to the filter would make the script's own inline writes retrigger it.
    console.log('\n[3] Hidden page copies');
    let dom = new JSDOM(
        '<!doctype html><html><head><style>.hide { display: none }</style></head><body>' +
        '  <div id="itemDetailPage" class="hide">' +
        '    <div class="itemExternalLinks">' +
        '      <a href="https://anidb.net/a1">aniDB</a>, ' +
        '      <a href="https://host:8096/web/#/details?id=' + SPECIAL_ID + '&serverId=s1&stm=s">TV Special</a>' +
        '    </div>' +
        '  </div>' +
        '  <div id="itemDetailPage2">' +
        '    <div class="itemExternalLinks visible-row">' +
        '      <a href="https://anidb.net/a2">aniDB</a>, ' +
        '      <a href="https://host:8096/web/#/details?id=' + MOVIE_ID + '&serverId=s1&stm=m">Movie Version</a>' +
        '    </div>' +
        '  </div>' +
        '</body></html>',
        { runScripts: 'dangerously', pretendToBeVisual: true, url: 'https://host:8096/web/' }
    );
    dom.window.eval(SCRIPT);
    await sleep(300);

    let doc = dom.window.document;
    const hiddenLink = doc.querySelector('#itemDetailPage a[href*="stm="]');
    const visibleLink = doc.querySelector('.visible-row a[href*="stm="]');
    check('hidden copy not rendered as an icon',
        !hiddenLink.classList.contains('specialtomovie-link'), 'class=' + hiddenLink.className);
    check('hidden copy carries no sizing', inlineProperties(hiddenLink).length === 0,
        inlineProperties(hiddenLink).join(','));
    check('visible copy rendered as an icon',
        visibleLink.classList.contains('specialtomovie-link'), 'class=' + visibleLink.className);

    // Hiding a page that was already styled has to strip it, because the same page is shown again.
    doc.querySelector('#itemDetailPage2').classList.add('hide');
    await sleep(300);
    check('styling stripped once the page is hidden',
        !visibleLink.classList.contains('specialtomovie-link'), 'class=' + visibleLink.className);

    // ---- 4. The reported bug: the first click, before the upgrade pass has run ----
    console.log('\n[4] Click on a not-yet-upgraded anchor (the new-tab bug)');
    dom = makePage({ logos: true });
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
    dom = makePage({ logos: true });
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
    dom = makePage({ logos: false });
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
    dom = makePage({ logos: false });
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

    // ---- 10. The link is separated from the one before it ----
    // The gap between links in this row is the whitespace between the anchors; every logo's own
    // margin sits inside its box. Moving the link to the end used to take its separator with it and
    // put nothing back, which butted the icon against the previous logo with no space at all.
    console.log('\n[10] Separator kept when moved to the end');
    dom = makePage({ logos: true });
    await sleep(300);
    row = rowOf(dom);
    let a = ours(dom);

    // Displace it, so the move path runs rather than the already-last shortcut.
    const pusher = dom.window.document.createElement('a');
    pusher.href = 'https://trakt.tv/y';
    pusher.textContent = 'Trakt';
    row.appendChild(pusher);
    await sleep(300);

    a = ours(dom);
    const before = a.previousSibling;
    check('preceded by a text node', !!before && before.nodeType === 3,
        'previous=' + (before ? before.nodeType : 'none'));
    check('that text node is whitespace', !!before && /\s/.test(before.nodeValue || ''),
        'value=' + JSON.stringify(before ? before.nodeValue : null));
    check('no run of separators left behind',
        !/[,;|]\s*[,;|]/.test(row.textContent), JSON.stringify(row.textContent.slice(0, 60)));

    // ---- 11. Nothing is written to the anchor but our own variables ----
    // The regression guard for the whole class of bug this replaced. Copying a neighbour's height,
    // width, display, margins and vertical-align onto the anchor as !important is what produced a
    // tile that sat low, jumped as it fought the other plugins in the row, and painted a second icon
    // inside itself. The row lays this link out now; nothing here may override that.
    console.log('\n[11] No geometry written to the anchor');
    dom = makePage({ logos: true });
    await sleep(300);
    a = ours(dom);
    const written = inlineProperties(a);
    const stray = written.filter((p) => ALLOWED_INLINE.indexOf(p) === -1);
    check('only our own custom properties are set inline', stray.length === 0,
        'stray=' + stray.join(',') + ' all=' + written.join(','));
    check('no inline colour', !a.style.color, 'color=' + a.style.color);
    check('no inline height or width', !a.style.height && !a.style.width,
        'h=' + a.style.height + ' w=' + a.style.width);
    check('no inline display or alignment',
        !a.style.display && !a.style.verticalAlign && !a.style.alignSelf,
        'display=' + a.style.display + ' va=' + a.style.verticalAlign);

    console.log('\n' + '='.repeat(46));
    console.log('pass ' + pass + '   fail ' + fail);
    process.exit(fail === 0 ? 0 : 1);
})();
