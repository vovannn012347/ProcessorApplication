/**
 * settings-index.js (Processor Module)
 * Handles AJAX form submission and lifecycle for Processor Configuration.
 */
(function () {
    const SCRIPT_ID = "processor-settings";
    const WRAPPER_ID = "settings-partial-wrapper";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing...`);
            this.attachFormListener(root);
        },

        /**
         * Scoped form listener attachment
         */
        attachFormListener: function (root) {
            const form = root.querySelector('#processorSettingsForm');
            if (!form) return;

            // Use direct assignment to ensure only one listener exists
            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleSave(root, form);
            };
        },

        /**
         * AJAX Save Handling
         */
        handleSave: function (root, form) {
            const btn = root.querySelector('#saveSettingsButton');
            const url = form.action;
            const formData = new FormData(form);

            const originalHtml = btn?.innerHTML;
            if (btn) {
                btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin pm-mr-2"></i> Saving...';
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

                    // Re-initialize logic for the new DOM (Element Hook pattern)
                    this.init(root);
                })
                .catch(error => {
                    if (btn) {
                        btn.innerHTML = originalHtml;
                        btn.disabled = false;
                    }
                    console.error(`[${SCRIPT_ID}] Update failed:`, error);
                    alert('Failed to save processor configuration.');
                });
        },

        /**
         * THE UNLOADING HOOK
         * @param {HTMLElement} el - The element being removed
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Unloading resources...`);
            // Empty signature for future cleanup (e.g. stopping active process logs)
            /*
            if (this.logPolling) {
                clearInterval(this.logPolling);
            }
            */
        }
    };

    // SELF-LAUNCHING BLOCK: Handles the first load or direct navigation
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    //# sourceURL=js/modules/processor/settings-index.js
})();