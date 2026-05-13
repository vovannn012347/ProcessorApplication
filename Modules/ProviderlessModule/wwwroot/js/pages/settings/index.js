/**
 * settings-index.js (Providerless Module)
 * Handles AJAX form submission and lifecycle for Portal Configuration.
 */
(function () {
    const SCRIPT_ID = "providerless-settings";
    const WRAPPER_ID = "providerless-settings-wrapper";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing settings logic...`);
            this.attachFormListener(root);
        },

        /**
         * Scoped form listener attachment
         */
        attachFormListener: function (root) {
            const form = root.querySelector('#providerlessSettingsForm');
            if (!form) return;

            // Use onclick or direct assignment to prevent listener stacking
            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleSave(root, form);
            };
        },

        /**
         * AJAX Save Handling
         */
        handleSave: function (root, form) {
            const btn = root.querySelector('#saveProviderlessSettingsButton');
            const url = form.action;
            const formData = new FormData(form);

            const originalHtml = btn?.innerHTML;
            if (btn) {
                btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin plss-mr-2"></i> Synchronizing...';
                btn.disabled = true;
            }

            fetch(url, {
                method: 'POST',
                body: formData,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(response => {
                    if (!response.ok) throw new Error(`Server returned status ${response.status}`);
                    return response.text();
                })
                .then(html => {
                    // Update the internal content of the wrapper
                    root.innerHTML = html;

                    // Re-initialize logic for the new DOM
                    this.init(root);

                    // Scroll to top of the settings container for UX
                    root.scrollIntoView({ behavior: 'smooth', block: 'start' });
                })
                .catch(error => {
                    if (btn) {
                        btn.innerHTML = originalHtml;
                        btn.disabled = false;
                    }
                    console.error(`[${SCRIPT_ID}] Update failed:`, error);
                    // In production, replace alert with a custom toast or message area
                    alert('Failed to save settings. Please verify the connection.');
                });
        },

        /**
         * THE UNLOADING HOOK
         * @param {HTMLElement} el - The element being removed
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Cleaning up settings resources.`);
            // Empty signature ready for future resource cleanup
        }
    };

    // SELF-LAUNCHING BLOCK: Handles the first load or direct navigation
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    //# sourceURL=js/modules/providerless/settings-index.js
})();