// Detail-page cross-link buttons: progressive enhancement over server-rendered links.
// See agentic/ARCHITECTURE.md, "Cross-link buttons", for every decision made here.
(function () {
    'use strict';

    // ! A second copy would add a second click handler and a second observer.
    if (window.__specialToMovieLoaded) {
        return;
    }

    window.__specialToMovieLoaded = true;

    var LOG_PREFIX = '[SpecialToMovie]';
    var STYLE_ID = 'specialtomovie-links-styles';
    var LINK_CLASS = 'specialtomovie-link';
    var UPGRADED_ATTR = 'data-stm-upgraded';
    var MOVES_ATTR = 'data-stm-moves';
    var SIZE_VAR = '--stm-icon-size';
    var GAP_VAR = '--stm-icon-gap';
    var SHIFT_VAR = '--stm-icon-shift';
    var MATCHED_ATTR = 'data-stm-matched';

    // A bigger correction than this is not a row the icon belongs in.
    var MAX_SHIFT = 40;

    // ! Our own marker, and nothing about the web client's markup. parseLink does the vetting.
    var LINK_SELECTOR = 'a[href*="stm="]';

    // The route and identifier shapes CrossLinkUrlBuilder emits.
    var DETAILS_ROUTE = '/details';
    var ITEM_ID = /^[0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12}$/;

    // Stops a plugin that also forces its links last from trading moves with us forever.
    var MAX_MOVES = 8;

    // Text nodes the web client puts between links. Jellyfin ships commas.
    var SEPARATOR = /^[\s,.;|·•-]+$/;

    // Links other plugins add to this row, skipped when measuring. Names from Jellyfin-Enhanced.
    var PLUGIN_LINK_CLASSES = [LINK_CLASS, 'letterboxd-link', 'seerr-link', 'arr-link',
        'arr-tag-link'];

    // What jellyfin-icon-metadata draws its logos at.
    var DEFAULT_ICON_SIZE = 25;

    // One inline SVG, so a server with no internet access still renders it.
    // A chain link on a green rounded square; see agentic/ARCHITECTURE.md for the palette.
    var ICON = 'data:image/svg+xml;utf8,' + encodeURIComponent(
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

    // ! The rule shape jellyfin-icon-metadata uses for every logo it draws.
    // The caption stays in the DOM for screen readers, hidden the way that project hides its own.
    function injectStyles() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent =
            '.' + LINK_CLASS + ' {' +
            '    background: none !important;' +
            '    color: transparent !important;' +
            '    padding: 0 !important;' +
            '    font-size: 0 !important;' +
            '}' +
            '.' + LINK_CLASS + '::before {' +
            '    content: "";' +
            '    display: inline-block;' +
            '    width: var(' + SIZE_VAR + ', ' + DEFAULT_ICON_SIZE + 'px);' +
            '    height: var(' + SIZE_VAR + ', ' + DEFAULT_ICON_SIZE + 'px);' +
            '    background-image: url("' + ICON + '");' +
            '    background-size: contain;' +
            '    background-repeat: no-repeat;' +
            // Matches the trailing space every logo in that row carries.
            '    margin-right: 5px;' +
            // Both written by matchRow; a relative offset keeps the line height untouched.
            '    margin-left: var(' + GAP_VAR + ', 0px);' +
            '    vertical-align: middle;' +
            '    position: relative;' +
            '    top: var(' + SHIFT_VAR + ', 0px);' +
            '}' +
            // ! No hover treatment of our own, and the theme's button chrome cancelled.
            // One tile reacting to the pointer in a row of logos that do not looks broken.
            '.' + LINK_CLASS + ':hover,' +
            '.' + LINK_CLASS + ':focus,' +
            '.' + LINK_CLASS + ':active {' +
            '    background: none !important;' +
            '    background-color: transparent !important;' +
            '    background-image: none !important;' +
            '    filter: none !important;' +
            '    border-color: transparent !important;' +
            '}';
        document.head.appendChild(style);
    }

    // ! Every field is held to the exact shape this plugin emits.
    // onClick cancels the browser default on whatever this accepts, for every click on the page.
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

        if (marker !== 'm' && marker !== 's') {
            return null;
        }

        if (!id || !ITEM_ID.test(id)) {
            return null;
        }

        return { marker: marker, id: id, hash: '#' + hash };
    }

    // A detail page is not deeply nested, and an unbounded walk runs per link per pass.
    var MAX_HIDDEN_HOPS = 20;

    // ! Asks the computed style, never the layout: an unlaid-out page has no rects either.
    // See agentic/ARCHITECTURE.md on the hidden detail-page copies.
    function isHidden(el) {
        var node = el;
        var hops = 0;

        while (node && node.nodeType === 1 && hops < MAX_HIDDEN_HOPS) {
            var style = window.getComputedStyle(node);
            if (style && (style.display === 'none' || style.visibility === 'hidden')) {
                return true;
            }

            node = node.parentElement;
            hops++;
        }

        return false;
    }

    // The height the row's own links render their content at, after Jellyfin-Enhanced.
    // ! The pseudo-element is asked first: a logo anchor's own rect has zero height.
    function iconSize(container, self) {
        var anchors = container.querySelectorAll('a');

        for (var i = 0; i < anchors.length; i++) {
            var anchor = anchors[i];
            if (anchor === self || isPluginLink(anchor)) {
                continue;
            }

            var pseudo = parseFloat(window.getComputedStyle(anchor, '::before').height);
            if (isFinite(pseudo) && pseudo > 0) {
                return pseudo;
            }

            var rect = anchor.getBoundingClientRect();
            var style = window.getComputedStyle(anchor);
            var chrome = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom) +
                parseFloat(style.borderTopWidth) + parseFloat(style.borderBottomWidth);
            var height = rect.height - (isFinite(chrome) ? chrome : 0);

            if (isFinite(height) && height > 0) {
                return height;
            }
        }

        return DEFAULT_ICON_SIZE;
    }

    // Where a link in this row is painted, vertically.
    // ! A zero-height rect is a baseline, and a logo's icon is centred on it.
    function visualCentre(el) {
        var rect = el.getBoundingClientRect();
        return rect.height > 0 ? rect.top + rect.height / 2 : rect.top;
    }

    function isPluginLink(anchor) {
        for (var i = 0; i < PLUGIN_LINK_CLASSES.length; i++) {
            if (anchor.classList.contains(PLUGIN_LINK_CLASSES[i])) {
                return true;
            }
        }

        return false;
    }

    // Last in the row, enforced every pass: the row is rebuilt on each render.
    // The move counter is the stop for another plugin that also insists on being last.
    function moveToEnd(anchor, container) {
        if (!container || container.lastElementChild === anchor) {
            // Settled, so forget the moves it took. Ordinary re-appends must not spend the budget.
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

        // A gap measured against the old neighbour describes nothing once we move.
        anchor.removeAttribute(MATCHED_ATTR);

        // Leaving it behind would leave a punctuation mark leading nowhere.
        dropSeparator(anchor);

        // ! The row's spacing is the whitespace between anchors, not anything they carry.
        // Jellyfin-Enhanced appends the same space before its own link.
        container.appendChild(document.createTextNode(' '));
        container.appendChild(anchor);
    }

    // The gap this row leaves between two of its own links, as a median.
    // Only pairs sharing a line count; a pair split across a wrap says nothing.
    function rowGap(container, self) {
        var links = container.querySelectorAll('a');
        var gaps = [];

        for (var i = 1; i < links.length; i++) {
            if (links[i] === self || links[i - 1] === self) {
                continue;
            }

            var a = links[i - 1].getBoundingClientRect();
            var b = links[i].getBoundingClientRect();
            if (Math.abs(b.top - a.top) < 2 && b.left >= a.right - 0.5) {
                gaps.push(b.left - a.right);
            }
        }

        if (!gaps.length) {
            return null;
        }

        gaps.sort(function (x, y) { return x - y; });
        return gaps[Math.floor(gaps.length / 2)];
    }

    // Sit this icon where the row's own links sit: same gap before it, same line.
    // ! Both corrections are measured, never assumed; see agentic/ARCHITECTURE.md.
    function matchRow(anchor, container) {
        if (anchor.hasAttribute(MATCHED_ATTR)) {
            return;
        }

        var previous = anchor.previousElementSibling;
        if (!previous) {
            // A row holding only this link is spaced by whatever precedes it.
            anchor.setAttribute(MATCHED_ATTR, '1');
            return;
        }

        // ! Zeroed first, or the measurement reads back its own correction.
        anchor.style.setProperty(GAP_VAR, '0px');
        anchor.style.setProperty(SHIFT_VAR, '0px');

        var theirs = previous.getBoundingClientRect();
        var mine = anchor.getBoundingClientRect();

        // Not laid out yet. Leaving the attribute unset lets the next pass try again.
        if (!theirs.width && !theirs.height && !mine.width && !mine.height) {
            return;
        }

        var shift = visualCentre(previous) - visualCentre(anchor);
        if (isFinite(shift) && Math.abs(shift) <= MAX_SHIFT) {
            anchor.style.setProperty(SHIFT_VAR, (Math.round(shift * 100) / 100) + 'px');
        }

        var target = rowGap(container, anchor);
        if (target !== null && Math.abs(mine.top - theirs.top) <= 2) {
            var correction = target - (mine.left - theirs.right);
            anchor.style.setProperty(GAP_VAR, (correction > 0.5 ? correction : 0) + 'px');
        }

        anchor.setAttribute(MATCHED_ATTR, '1');
    }

    // Exactly one separator beside the link: the one before it, or the one after when it leads.
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

    function upgradeLinks() {
        document.querySelectorAll(LINK_SELECTOR).forEach(function (anchor) {
            var link = parseLink(anchor.getAttribute('href'));
            if (!link) {
                return;
            }

            // ! Stripped, not merely skipped: a page hidden while styled is shown again later.
            if (isHidden(anchor)) {
                anchor.classList.remove(LINK_CLASS);
                anchor.style.removeProperty(SIZE_VAR);
                anchor.style.removeProperty(GAP_VAR);
                anchor.style.removeProperty(SHIFT_VAR);
                anchor.removeAttribute(MATCHED_ATTR);
                return;
            }

            var caption = (anchor.getAttribute('aria-label') ||
                anchor.textContent || '').trim();

            if (!anchor.hasAttribute(UPGRADED_ATTR)) {
                anchor.setAttribute('data-specialtomovie', link.marker === 's' ? 'special' : 'movie');
                anchor.setAttribute('data-linked-id', link.id);
                anchor.setAttribute(UPGRADED_ATTR, '1');
            }

            // The caption is hidden, not removed, so it has to stay reachable by name.
            // Re-applied every pass: a rebuilt anchor arrives with neither attribute.
            if (caption) {
                if (anchor.getAttribute('title') !== caption) {
                    anchor.setAttribute('title', caption);
                }

                if (anchor.getAttribute('aria-label') !== caption) {
                    anchor.setAttribute('aria-label', caption);
                }
            }

            // ! Reduced to a hash unconditionally, same-origin or not.
            // The web client rebuilds the row, and the fresh anchor has the absolute URL back.
            if (anchor.getAttribute('href') !== link.hash) {
                anchor.setAttribute('href', link.hash);
            }

            if (anchor.hasAttribute('target')) {
                anchor.removeAttribute('target');
            }

            var container = anchor.parentNode;

            // ! Asked every pass. A re-render resets className while keeping the attributes,
            // so a remembered "done" flag once sat over a link gone back to plain text.
            if (!anchor.classList.contains(LINK_CLASS)) {
                anchor.classList.add(LINK_CLASS);
            }

            if (container && container.querySelectorAll) {
                anchor.style.setProperty(SIZE_VAR, iconSize(container, anchor) + 'px');
            }

            moveToEnd(anchor, container);

            // After placement, to measure against the link this one ends up beside.
            if (container && container.querySelectorAll) {
                matchRow(anchor, container);
            }
        });
    }

    // ! Capture phase, so the click is ours whatever state the anchor is in.
    // A freshly rendered anchor still carries the absolute URL and target for a moment.
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

        // ! The external links container is rewritten on every render.
        // A style filter is deliberately absent: our own inline writes would retrigger it.
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
