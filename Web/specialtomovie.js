// SpecialToMovie — detail page cross-link buttons.
//
// The links themselves are rendered server-side by the plugin's IExternalUrlProvider
// implementations, so this script is pure progressive enhancement: it upgrades those anchors into
// icon buttons and rewrites them to navigate inside the app instead of opening a new tab.
// If anything here fails the links still work as plain text links.
(function () {
    'use strict';

    var LOG_PREFIX = '[SpecialToMovie]';
    var STYLE_ID = 'specialtomovie-links-styles';
    var UPGRADED_ATTR = 'data-stm-upgraded';

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
    // carry the same hash, so the hash is all this needs.
    function parseLink(href) {
        var hash = (href || '').split('#')[1];
        if (!hash) {
            return null;
        }

        var query = hash.split('?')[1];
        if (!query) {
            return null;
        }

        var params = new URLSearchParams(query);
        var marker = params.get('stm');
        var id = params.get('id');
        if (!marker || !id) {
            return null;
        }

        return { marker: marker, id: id, hash: '#' + hash };
    }

    // Is this row rendered as brand badges rather than text? Jellyfin ships text links; plugins
    // such as Jellyfin Enhanced can replace them with logo tiles, and that is a per-install, even
    // per-setting choice. Rather than detect a particular plugin by class name — which would break
    // the moment it renamed anything — ask the row what it currently looks like: a badge row is one
    // whose links render a picture and no words. A link showing an icon *and* text is still a text
    // row, so this stays false for Jellyfin Enhanced's icon-plus-text Letterboxd links.
    function rowIsBadges(container, self) {
        var anchors = container.querySelectorAll('a');
        var view = container.ownerDocument ? container.ownerDocument.defaultView : null;

        for (var i = 0; i < anchors.length; i++) {
            var other = anchors[i];
            if (other === self || (other.textContent || '').trim()) {
                continue;
            }

            if (other.querySelector && other.querySelector('img, svg, picture')) {
                return true;
            }

            if (view && view.getComputedStyle) {
                try {
                    if (view.getComputedStyle(other).backgroundImage !== 'none') {
                        return true;
                    }

                    var before = view.getComputedStyle(other, '::before').backgroundImage;
                    if (before && before !== 'none') {
                        return true;
                    }
                } catch (err) {
                    // A browser that will not compute styles here simply leaves this a text row.
                }
            }
        }

        return false;
    }

    // Our badge carries no text, so the ", " the web client puts between links would be left
    // dangling beside it. Drop exactly one — the one before it, or the one after when the badge
    // leads the row — so the remaining text links stay correctly punctuated.
    function dropSeparator(anchor) {
        var candidates = [anchor.previousSibling, anchor.nextSibling];

        for (var i = 0; i < candidates.length; i++) {
            var node = candidates[i];
            if (node && node.nodeType === 3 && /^[\s,]+$/.test(node.nodeValue || '')) {
                node.parentNode.removeChild(node);
                return;
            }
        }
    }

    function upgradeLinks() {
        // Stale upgrades on hidden detail pages would otherwise accumulate as the user navigates.
        document.querySelectorAll('#itemDetailPage.hide .specialtomovie-link').forEach(function (stale) {
            stale.classList.remove('specialtomovie-link', 'specialtomovie-badge');
            stale.removeAttribute(UPGRADED_ATTR);
        });

        var selector = '#itemDetailPage:not(.hide) .itemExternalLinks a[href*="stm="]:not([' + UPGRADED_ATTR + '])';
        document.querySelectorAll(selector).forEach(function (anchor) {
            var link = parseLink(anchor.getAttribute('href'));
            if (!link) {
                return;
            }

            anchor.classList.add('specialtomovie-link');
            anchor.setAttribute('data-specialtomovie', link.marker === 's' ? 'special' : 'movie');
            anchor.setAttribute('data-linked-id', link.id);

            // Reduce the link to its hash so the web client navigates in place instead of
            // reloading itself in a new tab. This is deliberately not conditional on the URL being
            // same-origin: these links always point at an item on the server that served this
            // page, so the hash is always the right way to reach it from here — and that stays
            // true when a reverse proxy hands the server a host name the browser cannot resolve,
            // which is exactly the case a same-origin test would get wrong.
            anchor.setAttribute('href', link.hash);
            anchor.removeAttribute('target');

            // Match the row. Among brand badges a lone text link looks like a mistake, and among
            // text links a lone badge looks like one just as much, so the row decides — not a
            // setting, and not an assumption about which plugins are installed.
            var container = anchor.parentNode;
            if (container && rowIsBadges(container, anchor)) {
                var caption = (anchor.textContent || '').trim();
                if (caption) {
                    anchor.setAttribute('title', caption);
                    anchor.setAttribute('aria-label', caption);
                }

                anchor.classList.add('specialtomovie-badge');
                dropSeparator(anchor);

                // Last in the row, where an extra badge reads as an addition to the set rather
                // than an interruption of it.
                container.appendChild(anchor);
            }

            anchor.setAttribute(UPGRADED_ATTR, '1');
        });
    }

    function start() {
        injectStyles();

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
