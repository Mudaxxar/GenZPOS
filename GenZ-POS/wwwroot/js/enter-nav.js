// Enter-key navigation for data entry: Enter moves to the next field instead of Tab.
//
// Where it applies: inside a <form> (RadzenTemplateForm), a dialog, or any element with data-enter-group.
// Search/filter boxes outside those keep normal Enter behaviour.
//
//  - Enter in a field        → focus next field (Shift+Enter → previous field; in a textarea, Shift+Enter = new line).
//  - Enter in the last field → focus the group's main button (type=submit, or [data-enter-submit]).
//  - Enter on that button    → clicks it (normal browser behaviour), then focus returns to the first field.
//  - While a dropdown/autocomplete/date popup is open, Enter is left to Radzen (selects the item).
//  - Buttons (+, Full, ×…) are skipped while moving between fields.
//  - Opt out a field with data-enter-skip (e.g. barcode box, where Enter must stay put).
(function () {
    const GROUP = 'form, .rz-dialog, [data-enter-group]';
    const FIELD = 'input, select, textarea, [tabindex]';
    const POPUP = '.rz-popup, .rz-dropdown-panel, .rz-autocomplete-panel, .rz-multiselect-panel, .rz-overlaypanel';

    function isVisible(el) {
        if (!el.isConnected) return false;
        const style = getComputedStyle(el);
        return style.display !== 'none' && style.visibility !== 'hidden' && el.getClientRects().length > 0;
    }

    // Is the focused control's *own* list open? Radzen dropdowns/autocompletes are role=combobox with aria-expanded.
    // Autocomplete: only hand Enter to Radzen when an item is highlighted; otherwise keep what was typed and move on.
    function ownListOpen(target) {
        const cb = target.closest('[role=combobox]');
        if (!cb || cb.getAttribute('aria-expanded') !== 'true') return false;
        if (!cb.closest('.rz-autocomplete')) return true;
        const listId = cb.getAttribute('aria-controls');
        const list = (listId && document.getElementById(listId)) || cb.closest('.rz-autocomplete')?.querySelector('.rz-autocomplete-panel');
        return !!list?.querySelector('.rz-state-highlight, [aria-selected=true]');
    }

    function isButton(el) {
        return el.tagName === 'BUTTON' || el.tagName === 'A' || el.getAttribute('role') === 'button'
            || (el.tagName === 'INPUT' && /^(button|submit|reset)$/i.test(el.type));
    }

    // Fields Enter can land on: focusable, visible, enabled, editable, not a button, not inside a popup.
    function fields(group) {
        return [...group.querySelectorAll(FIELD)].filter(el =>
            el.tabIndex >= 0 &&
            !el.disabled && !el.closest('[disabled], .rz-state-disabled') &&
            !(el.tagName === 'INPUT' && (el.type === 'hidden' || el.readOnly)) &&
            !isButton(el) &&
            !el.closest(POPUP) &&
            !el.closest('[data-enter-skip]') &&
            // a dropdown's inner hidden input: navigate to the dropdown itself instead
            !(el.tagName === 'INPUT' && el.closest('.rz-dropdown, .rz-multiselect') && !el.closest('.rz-dropdown-filter-container')) &&
            isVisible(el));
    }

    function submitButton(group) {
        return [...group.querySelectorAll('[data-enter-submit], button[type=submit]')]
            .find(b => !b.disabled && isVisible(b));
    }

    // The navigable element that "owns" the current focus (e.g. the dropdown div, not its inner span).
    function owner(list, target) {
        return list.find(el => el === target || el.contains(target));
    }

    function focus(el) {
        el.focus();
        if (el.select && el.tagName === 'INPUT' && /^(text|search|tel|number|email|)$/i.test(el.type)) {
            try { el.select(); } catch { /* some input types can't select */ }
        }
    }

    // After Add/Save: go back to [data-enter-first] if the group marks one, else its first field.
    // Blazor re-renders (and may replace) the form after saving, so retry for a moment and
    // re-find the group through its parent if the original element was swapped out.
    function focusFirstWhenReady(group) {
        const parent = group.parentElement;
        const index = parent ? [...parent.children].indexOf(group) : -1;
        const started = Date.now();

        const tryFocus = () => {
            const active = document.activeElement;
            if (active && active !== document.body && !isButton(active)) return; // user already moved on

            let g = group;
            if (!g.isConnected && parent && parent.isConnected) g = parent.children[index];
            if (!g || !g.isConnected) return; // e.g. dialog closed after Save

            const list = fields(g);
            const marked = g.querySelector('[data-enter-first]');
            // A marked barcode box (data-enter-skip, so not in the list) still gets focus back: its own input.
            const skipped = marked && marked.closest('[data-enter-skip]') ? marked.querySelector('input:not([disabled])') : null;
            const first = (marked && (owner(list, marked) || list.find(el => marked.contains(el)) || skipped)) || list[0];
            const busy = g.querySelector('.rz-busy'); // Radzen button spinner while saving
            if (first && !busy) { focus(first); return; }
            if (Date.now() - started < 2000) setTimeout(tryFocus, 100);
        };
        setTimeout(tryFocus, 150);
    }

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter' || e.ctrlKey || e.altKey || e.metaKey || e.isComposing) return;

        const target = e.target;
        if (!(target instanceof Element) || target.closest('[data-enter-skip]')) return;

        const group = target.closest(GROUP);
        if (!group) return;

        // Enter on the main button: let it click, then come back to the first field for the next entry.
        if (isButton(target)) {
            if (target.matches('[data-enter-submit], button[type=submit]'))
                focusFirstWhenReady(group);
            return;
        }

        if (target.tagName === 'TEXTAREA' && e.shiftKey) return; // new line

        // Never let Enter do the browser's implicit form submit. (Radzen's own key handlers still run.)
        e.preventDefault();

        // Typing in a dropdown's search box: Radzen picks the (first) matching item.
        if (target.closest(POPUP)) return;
        // This field's own list is open → let Radzen choose the item.
        if (ownListOpen(target)) return;

        const list = fields(group);
        const current = owner(list, target);
        if (!current) return;

        e.stopImmediatePropagation(); // don't let Radzen re-open the dropdown

        // (Moving focus blurs the field, which fires its change event, so Blazor gets the value.)
        const i = list.indexOf(current);
        if (e.shiftKey) {
            if (i > 0) focus(list[i - 1]);
            return;
        }
        if (i < list.length - 1) {
            focus(list[i + 1]);
        } else {
            const btn = submitButton(group);
            if (btn) btn.focus();
        }
    }, true); // capture phase: runs before Radzen's own key handlers
})();
