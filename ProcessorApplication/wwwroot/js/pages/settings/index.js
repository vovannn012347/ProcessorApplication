(function () {
    const SCRIPT_ID = "main-settings";
    const WRAPPER_ID = "settings-partial-wrapper";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        // Internal state
        activeTab: 'security',
        refreshInterval: null,

        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The specific element with data-script-id
         */
        init: function (el) {
            // Fallback to ID if el isn't provided (for direct loads)
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing on element...`, root);

            this.attachTabListeners(root);
            this.activateTab(root, this.activeTab);
            this.attachFormListener(root);

            // Example of a resource that would need disposal
            // this.refreshInterval = setInterval(() => console.log("Settings heartbeat..."), 30000);
        },

        /**
         * Activates a specific tab within the scope of the root element
         */
        activateTab: function (root, tabId) {
            if (!root) return;

            this.activeTab = tabId;
            root.querySelectorAll('.tab-button').forEach(btn => {
                const isActive = btn.getAttribute('data-tab') === tabId;
                btn.classList.toggle('mm-bg-indigo-600', isActive);
                btn.classList.toggle('mm-text-white', isActive);
                btn.classList.toggle('mm-shadow-md', isActive);
            });

            root.querySelectorAll('.tab-content').forEach(content => {
                content.classList.toggle('mm-hidden', content.id !== `tab-${tabId}`);
            });
        },

        /**
         * Scoped tab listener attachment
         */
        attachTabListeners: function (root) {
            root.querySelectorAll('.tab-button').forEach(btn => {
                btn.onclick = () => this.activateTab(root, btn.dataset.tab);
            });
        },

        /**
         * Scoped form listener attachment
         */
        attachFormListener: function (root) {
            const form = root.querySelector('form'); // Finds the masterSettingsForm within this root
            if (!form) return;

            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleSave(root, form);
            };
        },

        /**
         * AJAX Form Handling
         */
        handleSave: function (root, form) {
            const btn = root.querySelector('#masterSaveButton');
            const originalHtml = btn.innerHTML;
            btn.innerHTML = '<i class="fa-solid fa-sync fa-spin mm-mr-2"></i> Saving...';
            btn.disabled = true;

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.text())
                .then(html => {
                    // Since we are inside an AJAX-loaded partial, we update the root element's content
                    root.innerHTML = html;

                    // Re-initialize logic on the updated internal DOM of this root
                    this.init(root);
                })
                .catch(err => {
                    btn.innerHTML = originalHtml;
                    btn.disabled = false;
                    alert("Save failed: " + err.message);
                });
        },

        /**
         * THE UNLOADING HOOK
         * @param {HTMLElement} el - The element being removed
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Unloading element:`, el);

            // Commented sample of disposal logic:
            /*
            if (this.refreshInterval) {
                clearInterval(this.refreshInterval);
                this.refreshInterval = null;
            }
            */
        }
    };

    // If this is a direct load (non-AJAX), launch immediately
    const existing = document.getElementById(WRAPPER_ID);
    if (existing) {
        window.ModuleRegistry[SCRIPT_ID].init(existing);
    }

    //# sourceURL=js/modules/main-settings.js
})();