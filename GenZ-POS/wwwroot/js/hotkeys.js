// Function-key shortcuts for till-style screens.
// Mark an element with data-hotkey="F9": buttons are clicked, fields are focused
// (a dropdown with OpenOnFocus opens; for wrappers like RadzenNumeric the inner input gets focus).
(function () {
    const KEYS = ['F1', 'F2', 'F3', 'F4', 'F6', 'F7', 'F8', 'F9', 'F10', 'F11'];

    function visible(el) {
        return el.isConnected && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
    }

    function focusable(el) {
        if (el.matches('input, select, textarea, button, [tabindex]:not([tabindex="-1"])')) return el;
        return el.querySelector('input:not([type=hidden]), select, textarea, [tabindex]:not([tabindex="-1"])');
    }

    document.addEventListener('keydown', function (e) {
        if (!KEYS.includes(e.key) || e.ctrlKey || e.altKey || e.metaKey) return;

        // Inside an open dialog, only that dialog's hotkeys count.
        const dialogs = [...document.querySelectorAll('.rz-dialog')].filter(visible);
        const scope = dialogs.length ? dialogs[dialogs.length - 1] : document;

        const el = [...scope.querySelectorAll(`[data-hotkey="${e.key}"]`)].find(visible);
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
