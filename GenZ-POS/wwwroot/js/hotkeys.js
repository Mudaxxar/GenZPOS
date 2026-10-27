// Function-key shortcuts for till-style screens.
// Mark an element with data-hotkey="F9" (or a Ctrl combination, e.g. data-hotkey="Ctrl+R"): buttons are clicked,
// fields are focused (a dropdown with OpenOnFocus opens; for wrappers like RadzenNumeric the inner input gets focus).
// A Ctrl combination only counts when the screen has an element for it, so Ctrl+C / Ctrl+V keep working everywhere else.
(function () {
    const KEYS = ['F1', 'F2', 'F3', 'F4', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11'];

    function visible(el) {
        return el.isConnected && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
    }

    function focusable(el) {
        if (el.matches('input, select, textarea, button, [tabindex]:not([tabindex="-1"])')) return el;
        return el.querySelector('input:not([type=hidden]), select, textarea, [tabindex]:not([tabindex="-1"])');
    }

    // "F9", or "Ctrl+R" (letter in upper case), for the key pressed; null when it can't be a hotkey.
    function hotkeyName(e) {
        if (e.altKey || e.metaKey) return null;
        if (e.ctrlKey) return e.key.length === 1 ? 'Ctrl+' + e.key.toUpperCase() : null;
        return KEYS.includes(e.key) ? e.key : null;
    }

    document.addEventListener('keydown', function (e) {
        const name = hotkeyName(e);
        if (!name) return;

        // Inside an open dialog, only that dialog's hotkeys count.
        const dialogs = [...document.querySelectorAll('.rz-dialog')].filter(visible);
        const scope = dialogs.length ? dialogs[dialogs.length - 1] : document;

        const el = [...scope.querySelectorAll(`[data-hotkey="${name}"]`)].find(visible);
        if (!el) return;

        e.preventDefault();
        e.stopPropagation();

        const isButton = el.tagName === 'BUTTON' || el.getAttribute('role') === 'button';
        if (isButton) {
            if (!el.disabled) el.click();
            return;
        }

        const target = focusable(el);
        if (target) {
            target.focus();
            if (target.select && target.tagName === 'INPUT') {
                try { target.select(); } catch { /* not selectable */ }
            }
        }
    }, true);
})();
