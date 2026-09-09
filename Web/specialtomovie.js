// SpecialToMovie — detail page cross-link buttons.
//
// The links themselves are rendered server-side by the plugin's IExternalUrlProvider
// implementations, so this script is pure progressive enhancement: it upgrades those anchors into
// icon buttons, keeps them at the end of the row, and navigates in-app instead of opening a new
// tab. If anything here fails the links still work as plain text links.
(function () {
    'use strict';

    // The injector adds the tag once per document, but a second copy - a stale service worker, a
    // manual install into the web root alongside the injected tag - would otherwise register a
    // second click handler and a second MutationObserver on the same page.
    if (window.__specialToMovieLoaded) {
        return;
    }

    window.__specialToMovieLoaded = true;

    var LOG_PREFIX = '[SpecialToMovie]';
    var STYLE_ID = 'specialtomovie-links-styles';
    var UPGRADED_ATTR = 'data-stm-upgraded';
    var MOVES_ATTR = 'data-stm-moves';

    // Our own marker is the only thing this matches on. An earlier version also required the link
    // to sit inside `#itemDetailPage:not(.hide) .itemExternalLinks`, which tied the whole
    // enhancement to two web-client class names; if either changed, the script silently did nothing
    // and every link fell back to unstyled text that opened a new tab. The marker is ours, so it
    // cannot drift, and parseLink rejects anything that merely happens to contain it.
    var LINK_SELECTOR = 'a[href*="stm="]';

    // The route and identifier shapes CrossLinkUrlBuilder emits. parseLink holds incoming links to
    // exactly these.
    var DETAILS_ROUTE = '/details';
    var ITEM_ID = /^[0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12}$/;

    // Another plugin that also forces its links last would otherwise trade moves with us forever.
    var MAX_MOVES = 8;

    // How far up the tree to look for the row of brand badges. Four hops clears the wrapper
    // elements a converting plugin realistically adds without widening the search to the page.
    var MAX_ROW_HOPS = 4;

    // Text nodes the web client puts between links. Commas are what Jellyfin ships; the others are
    // here because a separator left dangling beside a badge looks broken, and which character a
    // given client or theme uses is not worth being wrong about.
    var SEPARATOR = /^[\s,.;|·•-]+$/;

    // One badge tile, inline so it renders on a server with no internet access. It is a complete
    // self-coloured mark rather than a tinted glyph: the row it joins is a row of brand badges
    // (IMDb, TMDB, Trakt, Jellyseerr), and a badge that recoloured itself with the theme would not
    // belong there. Rounded square with a gentle gradient, sized and shaped like the logos beside
    // it. Green because nothing else in that row is, so it reads as its own thing at a glance.
    // The gradient runs light to dark across the diagonal, perpendicular to the chain, so the
    // fall is visible on both sides of the glyph. The stops are deliberately far apart: at 28px on
    // a phone a timid gradient is indistinguishable from flat colour, and a gradient nobody can see
    // is not worth having. The range is spent on the dark end — a lighter light stop would start
    // washing out the white chain, and the glyph reading clearly matters more than the sheen does.
    //
    // The mark is a chain link, drawn along the bottom-left to top-right diagonal so its axis runs
    // corner to corner. Both directions of the pairing share it, exactly as each service in that
    // row has one logo; which way it goes is obvious from the page you are on, and the tooltip and
    // accessible name say so outright.
    var BADGE = 'data:image/svg+xml;utf8,' + encodeURIComponent(
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">' +
        '<defs><linearGradient id="a" x1="0" y1="0" x2="32" y2="32" ' +
        'gradientUnits="userSpaceOnUse">' +
        '<stop stop-color="#5EDC86"/><stop offset="1" stop-color="#07602D"/>' +
        '</linearGradient></defs>' +
        '<rect width="32" height="32" rx="7" fill="url(#a)"/>' +
        '<g transform="translate(16 16) scale(.82) translate(-12 -12)" fill="none" ' +
        'stroke="#fff" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">' +
        '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/>' +
        '<path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>' +
        '</g></svg>');

    function injectStyles() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent =
            '.specialtomovie-badge {' +
            '    display: inline-block;' +
            '    width: 28px;' +
            '    height: 28px;' +
            // Matches the tile's own corner radius, so the box and the artwork agree.
            '    border-radius: 6px;' +
            '    vertical-align: middle;' +
            '    margin: 0 2px;' +
            '    background-image: url("' + BADGE + '");' +
            '    background-repeat: no-repeat;' +
            '    background-position: center;' +
            '    background-size: 100% 100%;' +
            // The caption stays in the accessible name; only its rendering is suppressed, and this
            // holds the moment the web client re-renders the row and hands the text back.
            '    text-indent: -9999px;' +
            '    overflow: hidden;' +
            '    white-space: nowrap;' +
            '    transition: transform .12s ease, filter .12s ease;' +
            '}' +
            '.specialtomovie-badge:hover, .specialtomovie-badge:focus-visible {' +
            '    filter: brightness(1.12);' +
            '    transform: translateY(-1px);' +
            '}' +
            '.specialtomovie-badge:focus-visible {' +
            '    outline: 2px solid #5EDC86;' +
            '    outline-offset: 2px;' +
            '}' +
            '@media (prefers-reduced-motion: reduce) {' +
            '    .specialtomovie-badge { transition: none; }' +
            '    .specialtomovie-badge:hover, .specialtomovie-badge:focus-visible {' +
            '        transform: none;' +
            '    }' +
            '}';
        document.head.appendChild(style);
    }

    // The server emits a full URL so that phone, tablet and TV apps can follow it. Inside the web
    // client that form is the wrong one: it opens a new tab and reloads the whole app. Both forms
    // carry the same hash, so the hash is all this needs — which is also what lets the click
    // handler below work on an anchor that has not been upgraded yet.
    function parseLink(href) {
        var hash = (href || '').split('#')[1];
        if (!hash) {
            return null;
        }

        var parts = hash.split('?');
        if (parts[0] !== DETAILS_ROUTE || !parts[1]) {
            return null;
        }

        var params = new URLSearchParams(parts[1]);
        var marker = params.get('stm');
        var id = params.get('id');

        // Every field is checked against the exact shape this plugin emits, not merely for being
        // present. The click handler below cancels the browser's default on whatever this accepts,
        // and it sees every click in the document, so a loose match here would let it swallow
        // another plugin's link. Only the plugin's own route, its two markers and an item GUID pass.
        if (marker !== 'm' && marker !== 's') {
            return null;
        }

        if (!id || !ITEM_ID.test(id)) {
            return null;
        }

        return { marker: marker, id: id, hash: '#' + hash };
    }

    function computed(view, el, pseudo) {
        if (!view || !view.getComputedStyle) {
            return null;
        }

        try {
            return view.getComputedStyle(el, pseudo || null);
        } catch (err) {
            return null;
        }
    }

    function pseudoPaintsImage(anchor, view) {
        var pseudos = ['::before', '::after'];
        for (var i = 0; i < pseudos.length; i++) {
            var ps = computed(view, anchor, pseudos[i]);
            if (ps && ps.backgroundImage && ps.backgroundImage !== 'none') {
                return true;
            }
        }

        return false;
    }

    // Is a caption present but not painted? The usual way to turn a text link into a logo is to
    // keep the label for screen readers and push it out of view, so a label existing says nothing
    // about how the row reads; only whether it is painted does. The old detector treated any anchor
    // carrying text as a text link, which meant that on exactly the badge rows this is meant to
    // recognise, every anchor was skipped and the row was judged to be text.
    function captionSuppressed(anchor, style) {
        if (style) {
            if (parseFloat(style.textIndent) <= -999) {
                return true;
            }

            if (parseFloat(style.fontSize) === 0) {
                return true;
            }
        }

        // Falls back to shape for the case CSS cannot answer: the label is hidden on a child rather
        // than on the anchor, so the anchor still reports normal text metrics. A logo tile is
        // roughly square; an icon sitting beside real words is much wider than it is tall, which is
        // what keeps this false for icon-plus-text links.
        if (anchor.getBoundingClientRect) {
            var rect = anchor.getBoundingClientRect();
            if (rect.height > 0 && rect.width > 0 && rect.width <= rect.height * 2.5) {
                return true;
            }
        }

        return false;
    }

    // Does this one anchor read as a brand badge - a picture and no visible words? The tests are
    // ordered cheapest first, and deliberately so: this runs for every link in the row on every
    // pass, and both getComputedStyle with a pseudo-element and getBoundingClientRect make the
    // browser resolve style or layout. On a plain text row - the common case, and the one where the
    // answer is no - the early exits hold it to a single style read per link and no layout at all.
    function isBadgeAnchor(anchor, view) {
        var hasPicture = !!(anchor.querySelector && anchor.querySelector('img, svg, picture'));
        var hasText = !!(anchor.textContent || '').trim();

        if (hasPicture && !hasText) {
            return true;
        }

        var style = computed(view, anchor);
        if (!hasPicture) {
            hasPicture = !!(style && style.backgroundImage && style.backgroundImage !== 'none');
        }

        if (!hasText) {
            return hasPicture || pseudoPaintsImage(anchor, view);
        }

        // A link with words is a text link unless it paints a picture and hides the words.
        return hasPicture && captionSuppressed(anchor, style);
    }

    // The first badge-like link inside this subtree, or null. Returning the element rather than a
    // boolean is what lets the caller both join the row it belongs to and copy its measurements.
    function findBadgeIn(root, self) {
        if (!root || !root.querySelectorAll) {
            return null;
        }

        var anchors = root.querySelectorAll('a');
        var view = root.ownerDocument ? root.ownerDocument.defaultView : null;

        for (var i = 0; i < anchors.length; i++) {
            var other = anchors[i];
            if (other === self || other.classList.contains('specialtomovie-link')) {
                continue;
            }

            if (isBadgeAnchor(other, view)) {
                return other;
            }
        }

        return null;
    }

    // Find the row of brand badges this link should join, if there is one.
    //
    // Looking only at the link's own parent was not enough. A plugin that converts the text links
    // into logo tiles may put those tiles in a container of its own, leaving the plugin's link
    // behind in the original one - and a parent holding nothing but our link looks exactly like a
    // text row, so the link stayed text on one side of the pair while rendering correctly on the
    // other. Searching upwards finds the badges wherever they were put; returning the badge itself
    // means the link is then moved in beside them rather than being styled to look like a tile from
    // outside the row, which is what alignment actually depends on.
    //
    // The walk is bounded because the search widens at every hop: far enough up, every link on the
    // page is in scope and any badge anywhere would count as proof this row is badges.
    function findBadgeRow(anchor) {
        var node = anchor.parentNode;
        var hops = 0;

        while (node && node.nodeType === 1 && node !== document.body && hops < MAX_ROW_HOPS) {
            var badge = findBadgeIn(node, anchor);
            if (badge && badge.parentNode) {
                return { badge: badge, container: badge.parentNode };
            }

            node = node.parentNode;
            hops++;
        }

        return null;
    }

    // Properties copied from a neighbouring badge so the tile sits on the same line as the rest of
    // the row. Kept as a list because the text form has to remove exactly what the badge form set.
    var MATCHED_PROPERTIES = [
        'height', 'width', 'display', 'vertical-align', 'align-self',
        'margin-top', 'margin-bottom', 'margin-left', 'margin-right'
    ];

    // Size and align the tile from a real neighbour instead of a fixed 28px. The row's logos are
    // whatever height that install's theme and plugins make them, so a hard-coded box lines up only
    // by luck - it was visibly out of line against a real row. The tile stays square: it is a mark,
    // not a wordmark, so it matches the row's height and not any particular logo's width.
    function matchBadgeMetrics(anchor, badge) {
        var view = anchor.ownerDocument ? anchor.ownerDocument.defaultView : null;
        var style = computed(view, badge);
        if (!style) {
            return;
        }

        var rect = badge.getBoundingClientRect ? badge.getBoundingClientRect() : null;
        if (rect && rect.height > 0) {
            anchor.style.setProperty('height', rect.height + 'px', 'important');
            anchor.style.setProperty('width', rect.height + 'px', 'important');
        }

        // 'inline' would collapse a box whose content is a background image, and 'none' would hide
        // it outright; anything else the row uses is worth matching.
        var display = style.display;
        if (display && display !== 'inline' && display !== 'none') {
            anchor.style.setProperty('display', display, 'important');
        }

        ['vertical-align', 'align-self', 'margin-top', 'margin-bottom',
            'margin-left', 'margin-right'].forEach(function (prop) {
            var value = style.getPropertyValue(prop);
            if (value) {
                anchor.style.setProperty(prop, value, 'important');
            }
        });
    }

    function clearBadgeMetrics(anchor) {
        for (var i = 0; i < MATCHED_PROPERTIES.length; i++) {
            anchor.style.removeProperty(MATCHED_PROPERTIES[i]);
        }
    }

    function parseRgb(value) {
        var match = /^rgba?\(([^)]+)\)/.exec(value || '');
        if (!match) {
            return null;
        }

        var parts = match[1].split(/[,\s/]+/).filter(Boolean).map(parseFloat);
        if (parts.length < 3 || parts.some(isNaN)) {
            return null;
        }

        return {
            r: parts[0],
            g: parts[1],
            b: parts[2],
            a: parts.length > 3 ? parts[3] : 1
        };
    }

    // What is actually behind this link? Walks up until something paints an opaque background.
    // The theme is not knowable from prefers-color-scheme here: Jellyfin themes are chosen in the
    // app, so a light theme on a machine set to dark is perfectly normal and vice versa. The only
    // reliable answer is what the pixels behind the text are, so that is what this asks for, with
    // the OS preference kept as a last resort for when nothing up the tree is opaque.
    function backdropIsDark(anchor) {
        var view = anchor.ownerDocument ? anchor.ownerDocument.defaultView : null;
        var node = anchor.parentElement;

        while (node && node.nodeType === 1) {
            var style = computed(view, node);
            var colour = style && parseRgb(style.backgroundColor);
            if (colour && colour.a >= 0.5) {
                var luminance = (0.2126 * colour.r + 0.7152 * colour.g + 0.0722 * colour.b) / 255;
                return luminance < 0.5;
            }

            node = node.parentElement;
        }

        if (view && view.matchMedia) {
            try {
                return !view.matchMedia('(prefers-color-scheme: light)').matches;
            } catch (err) {
                // Falls through to the default below.
            }
        }

        return true;
    }

    // Only for the text form; the badge paints its own colours and must not be touched. Set as an
    // important inline value because what it overrides is the web client's own styling for links in
    // this row, which an ordinary inline value does not always outrank.
    function applyContrastColour(anchor) {
        var colour = backdropIsDark(anchor) ? '#e9e9e9' : '#1c1c1c';
        try {
            anchor.style.setProperty('color', colour, 'important');
        } catch (err) {
            anchor.style.color = colour;
        }
    }

    // Our badge carries no text, so the ", " the web client puts between links would be left
    // dangling beside it. Drop exactly one — the one before it, or the one after when the badge
    // leads the row — so the remaining text links stay correctly punctuated.
    function dropSeparator(anchor) {
        var candidates = [anchor.previousSibling, anchor.nextSibling];

        for (var i = 0; i < candidates.length; i++) {
            var node = candidates[i];
            if (node && node.nodeType === 3 && SEPARATOR.test(node.nodeValue || '')) {
                node.parentNode.removeChild(node);
                return;
            }
        }
    }

    // Last in the row, where an extra link reads as an addition to the set rather than an
    // interruption of it. Enforced on every pass rather than once at upgrade time: the row is
    // rebuilt on each render and other plugins append to it after we do, so a position set once
    // does not stay set. The move counter is the stop for the pathological case of another plugin
    // that also insists on being last — without it the two of us would rewrite the DOM forever.
    function moveToEnd(anchor, container, asBadge) {
        if (!container) {
            return;
        }

        // Already last: the row is settled, so forget any moves it took to get here. Without this
        // the budget is spent by ordinary re-appends over a long session and the link eventually
        // stops being placed at all. A genuine fight never reaches this line, so it still stops.
        if (container.lastElementChild === anchor) {
            if (anchor.hasAttribute(MOVES_ATTR)) {
                anchor.removeAttribute(MOVES_ATTR);
            }

            return;
        }

        var moves = parseInt(anchor.getAttribute(MOVES_ATTR) || '0', 10) || 0;
        if (moves >= MAX_MOVES) {
            return;
        }

        anchor.setAttribute(MOVES_ATTR, String(moves + 1));

        // Take the separator that belonged to the old position with it, or the row is left with a
        // comma leading nowhere.
        dropSeparator(anchor);
        container.appendChild(anchor);

        // A text link at the end still needs to be joined to the list it now ends.
        if (!asBadge) {
            var prev = anchor.previousSibling;
            var joined = prev && prev.nodeType === 3 && SEPARATOR.test(prev.nodeValue || '');
            if (!joined && prev) {
                container.insertBefore(document.createTextNode(', '), anchor);
            }
        }
    }

    function upgradeLinks() {
        document.querySelectorAll(LINK_SELECTOR).forEach(function (anchor) {
            var link = parseLink(anchor.getAttribute('href'));
            if (!link) {
                return;
            }

            if (!anchor.hasAttribute(UPGRADED_ATTR)) {
                anchor.classList.add('specialtomovie-link');
                anchor.setAttribute('data-specialtomovie', link.marker === 's' ? 'special' : 'movie');
                anchor.setAttribute('data-linked-id', link.id);
                anchor.setAttribute(UPGRADED_ATTR, '1');
            }

            // Reduce the link to its hash so the web client navigates in place instead of
            // reloading itself in a new tab. This is deliberately not conditional on the URL being
            // same-origin: these links always point at an item on the server that served this
            // page, so the hash is always the right way to reach it from here — and that stays
            // true when a reverse proxy hands the server a host name the browser cannot resolve,
            // which is exactly the case a same-origin test would get wrong.
            //
            // Re-applied on every pass rather than once, because the web client rebuilds this row
            // from the server DTO and the fresh anchor arrives with the absolute href and
            // target="_blank" restored.
            if (anchor.getAttribute('href') !== link.hash) {
                anchor.setAttribute('href', link.hash);
            }

            if (anchor.hasAttribute('target')) {
                anchor.removeAttribute('target');
            }

            // Match the row. Among brand badges a lone text link looks like a mistake, and among
            // text links a lone badge looks like one just as much, so the row decides — not a
            // setting, and not an assumption about which plugins are installed.
            var row = findBadgeRow(anchor);
            var asBadge = !!row;
            var container = asBadge ? row.container : anchor.parentNode;

            if (asBadge) {
                var caption = (anchor.textContent || '').trim();
                if (caption) {
                    anchor.setAttribute('title', caption);
                    anchor.setAttribute('aria-label', caption);
                }

                anchor.classList.add('specialtomovie-badge');
                anchor.style.removeProperty('color');
                matchBadgeMetrics(anchor, row.badge);
            } else {
                anchor.classList.remove('specialtomovie-badge');
                clearBadgeMetrics(anchor);
                applyContrastColour(anchor);
            }

            moveToEnd(anchor, container, asBadge);
        });
    }

    // Navigation is handled here rather than left to the rewritten href alone. The row is rebuilt
    // on every render, so there is always a window in which a freshly rendered anchor still carries
    // the absolute URL and target="_blank"; a click landing inside that window opened a new tab,
    // and only the second click — on the anchor the upgrade had by then caught up with — stayed in
    // the app. Listening in the capture phase means the click is ours before the anchor's own
    // default runs, whatever state the anchor is in, and parseLink reads the item out of the
    // absolute URL just as well as out of the rewritten hash.
    function onClick(event) {
        if (event.defaultPrevented || event.button !== 0 ||
            event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            // A deliberate open-in-new-tab is still the user's to make.
            return;
        }

        var node = event.target;
        while (node && node !== document && !(node.nodeType === 1 && node.tagName === 'A')) {
            node = node.parentNode;
        }

        if (!node || node === document || node.nodeType !== 1) {
            return;
        }

        var link = parseLink(node.getAttribute('href'));
        if (!link) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();

        try {
            window.location.hash = link.hash;
        } catch (err) {
            console.warn(LOG_PREFIX, 'failed to navigate to the linked item', err);
        }
    }

    function start() {
        injectStyles();

        document.addEventListener('click', onClick, true);

        var pending = false;
        var schedule = function () {
            if (pending) {
                return;
            }

            pending = true;
            var run = function () {
                pending = false;
                try {
                    upgradeLinks();
                } catch (err) {
                    console.warn(LOG_PREFIX, 'failed to upgrade cross-links', err);
                }
            };

            if (typeof requestIdleCallback !== 'undefined') {
                requestIdleCallback(run, { timeout: 500 });
            } else {
                setTimeout(run, 100);
            }
        };

        // The web client rewrites the external links container on every render, so the upgrade has
        // to be re-applied rather than done once.
        new MutationObserver(schedule).observe(document.body, {
            childList: true,
            subtree: true,
            attributeFilter: ['class']
        });

        schedule();
    }

    try {
        if (document.body) {
            start();
        } else {
            document.addEventListener('DOMContentLoaded', start);
        }
    } catch (err) {
        console.warn(LOG_PREFIX, 'initialisation failed', err);
    }
})();
