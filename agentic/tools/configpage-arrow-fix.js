// Puts each stray emby-select chevron back on its own control, from the browser console, on a
// server running a build whose config page does not wrap its selects.
//
// Jellyfin draws a select's chevron in a .selectArrowContainer, which is position: absolute. A
// select whose parent is static gives that container no containing block, so every such arrow
// resolves against the page and stacks in its top-right corner. This wraps each one the way
// configPage.html now does, and moves the arrow into the wrapper with it.
//
// ! The arrow is not reliably the select's next sibling -- emby-select's placement varies. Arrows
// are paired to selects by their order inside the shared parent instead.
//
// Paste the whole file into the DevTools console on the plugin's settings page. It lasts until the
// page is reloaded; __stmArrowFix.undo() puts the DOM back as it was.
//
// Checked against a released page with:
//   node configpage-selects.js path/to/that/configPage.html configpage-arrow-fix.js
(function () {
    'use strict';

    if (window.__stmArrowFix) { window.__stmArrowFix.undo(); }

    var WRAP_CLASS = 'stm-arrow-fix-wrapper';
    var ARROW = 'selectArrowContainer';
    var wrapped = [];
    var held = 0;
    var orphans = 0;

    var isArrow = function (el) {
        return el.nodeType === 1 && el.classList.contains(ARROW);
    };

    // Selects sharing a static parent, which is the only case that loses an arrow.
    var groups = [];
    [].slice.call(document.querySelectorAll('select[is="emby-select"]')).forEach(function (sel) {
        var parent = sel.parentElement;
        if (!parent) { return; }

        if (window.getComputedStyle(parent).position !== 'static') {
            held++;
            return;
        }

        var group = null;
        for (var i = 0; i < groups.length; i++) {
            if (groups[i].parent === parent) { group = groups[i]; break; }
        }

        if (!group) {
            group = { parent: parent, selects: [] };
            groups.push(group);
        }

        group.selects.push(sel);
    });

    groups.forEach(function (group) {
        var arrows = [].slice.call(group.parent.children).filter(isArrow);

        if (arrows.length !== group.selects.length) {
            console.warn('[STM arrow fix] ' + group.selects.length + ' select(s) but ' +
                arrows.length + ' arrow(s) in this row. Children: ' +
                [].slice.call(group.parent.children).map(function (el) {
                    return el.tagName.toLowerCase() + '.' + (el.className || '-');
                }).join(' '));
        }

        group.selects.forEach(function (sel, index) {
            var arrow = arrows[index] || null;
            var before = arrow ? arrow.getBoundingClientRect() : null;

            var wrap = document.createElement('div');
            wrap.className = WRAP_CLASS;
            wrap.style.cssText = 'position:relative;display:inline-block;';

            group.parent.insertBefore(wrap, sel);
            wrap.appendChild(sel);

            if (arrow) {
                wrap.appendChild(arrow);
                var after = arrow.getBoundingClientRect();
                console.log('[STM arrow fix] ' + (sel.id || '(no id)') + ': arrow ' +
                    Math.round(before.left) + ',' + Math.round(before.top) + ' -> ' +
                    Math.round(after.left) + ',' + Math.round(after.top));
            } else {
                orphans++;
                console.warn('[STM arrow fix] ' + (sel.id || '(no id)') + ': no arrow found for it');
            }

            wrapped.push(wrap);
        });
    });

    // Anything still loose in a static parent would stay in the corner.
    var stranded = [].slice.call(document.querySelectorAll('.' + ARROW)).filter(function (el) {
        return el.parentElement &&
            window.getComputedStyle(el.parentElement).position === 'static';
    }).length;

    window.__stmArrowFix = {
        undo: function () {
            wrapped.forEach(function (wrap) {
                var parent = wrap.parentElement;
                if (!parent) { return; }

                while (wrap.firstChild) {
                    parent.insertBefore(wrap.firstChild, wrap);
                }

                parent.removeChild(wrap);
            });

            wrapped = [];
            delete window.__stmArrowFix;
            console.log('[STM arrow fix] undone. Reload for the page as the server sent it.');
        },
        get wrapped() { return wrapped.length; }
    };

    console.log('[STM arrow fix] wrapped ' + wrapped.length + ' select(s); ' + held +
        ' already held their arrow; ' + orphans + ' without an arrow; ' + stranded +
        ' arrow(s) still loose. The corner is clear when that last number is 0. ' +
        'Undo with __stmArrowFix.undo()');
})();
