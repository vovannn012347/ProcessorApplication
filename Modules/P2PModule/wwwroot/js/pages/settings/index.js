/**
 * index.js (P2P Module Settings Engine)
 * Handles forms tracking orchestration inside the sandboxed p2p lifecycle.
 */
(function () {
    const SCRIPT_ID = "p2p-settings";
    const WRAPPER_ID = "p2p-settings-wrapper";

    root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Initialization Context Entry Point
         * @param {HTMLElement} el - Context container binding node
         */
        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Registering settings lifecycle hooks.`);
            this.attachFormListener(root);
            this.initAutoDiscoverySubsystem(root);
        },

        /**
         * Context form listener attachment
         */
        attachFormListener: function (root) {
            const form = root.querySelector('#p2pSettingsForm');
            if (!form) return;

            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleSave(root, form);
            };
        },

        /**
         *Endpoint listerer attachment
         */
        initAutoDiscoverySubsystem: function (container) {
            const toggleBtn = container.querySelector('#p2pDropdownToggleBtn');
            const menu = container.querySelector('#p2pDropdownMenu');
            const itemsList = container.querySelector('#p2pDropdownItemsList');
            const targetTextArea = container.querySelector('#p2pNodeAccessTextArea');

            if (!toggleBtn || !menu || !itemsList || !targetTextArea) return;

            // Simple Local Dropdown Toggle
            toggleBtn.onclick = (e) => {
                e.stopPropagation();
                const isHidden = menu.classList.contains('p2p-hidden');
                if (isHidden) {
                    menu.classList.remove('p2p-hidden');
                } else {
                    menu.classList.add('p2p-hidden');
                }
            };

            // Global Closer Strategy
            document.addEventListener('click', () => menu.classList.add('p2p-hidden'));

            // Click Bubbling for Actions Rendered Server-Side
            itemsList.onclick = (e) => {
                const targetBtn = e.target.closest('[data-action]');
                if (!targetBtn) return;

                const actionKey = targetBtn.getAttribute('data-action');

                // Dispatch execution lookup to resolve target network string boundaries
                fetch(`/P2P/Settings/ResolveDiscoveryEndpoint?actionKey=${encodeURIComponent(actionKey)}`, {
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => r.json())
                    .then(data => {
                        if (!data || !data.endpoint) return;

                        // Split current list while discarding orphan elements
                        let currentEntries = targetTextArea.value.split(',')
                            .map(v => v.trim())
                            .filter(v => v.length > 0);

                        // Enforce uniqueness constraints locally
                        if (!currentEntries.includes(data.endpoint)) {
                            currentEntries.push(data.endpoint);
                            targetTextArea.value = currentEntries.join(', ');
                        }
                    })
                    .catch(err => console.error(`[${SCRIPT_ID}] Resolution tracking error:`, err));
            };
        },

        /**
         * Dispatches async configuration changes back to backend controller context
         */
        handleSave: function (root, form) {
            const btn = root.querySelector('#saveP2PSettingsButton');
            const url = form.action;
            const formData = new FormData(form);

            var originalHtml = "";
            if (btn) {
                originalHtml = btn.innerHTML;
                btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin p2p-mr-2"></i> Saving Settings...';
                btn.disabled = true;
            }

            fetch(url, {
                method: 'POST',
                body: formData,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(response => {
                    if (!response.ok) throw new Error(`Server transaction failure. HTTP Status: ${response.status}`);
                    return response.text();
                })
                .then(html => {
                    // Safely update live container fragment
                    root.innerHTML = html;

                    // Re-bind lifecycle context constraints
                    this.init(root);

                    root.scrollIntoView({ behavior: 'smooth', block: 'start' });
                })
                .catch(error => {
                    if (btn) {
                        btn.innerHTML = originalHtml;
                        btn.disabled = false;
                    }
                    console.error(`[${SCRIPT_ID}] Persistence failure logged:`, error);
                    alert('Failed to save settings. Please verify configuration properties.');
                });
        },

        /**
         * Standard unbinding teardown hook
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Purging module configurations.`);
        }
    };

    // Self-launch trigger tracking context
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    //# sourceURL=js/modules/p2p/settings-index.js
})();