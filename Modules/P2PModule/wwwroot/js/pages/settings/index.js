/**
 * index.js (P2P Module Settings Engine)
 * Handles forms tracking orchestration inside the sandboxed p2p lifecycle.
 */
(function () {
    const SCRIPT_ID = "p2p-settings";
    const WRAPPER_ID = "p2p-settings-wrapper";

    let root = {}; // local to this script (was an implicit global shared by all page scripts)
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
            const addBtn = container.querySelector('#p2pAddEndpointBtn');
            const source = container.querySelector('#p2pEndpointSource');
            const targetTextArea = container.querySelector('#p2pNodeAccessTextArea');
            if (!addBtn || !source || !targetTextArea) return;

            addBtn.onclick = () => {
                const actionKey = source.value;
                if (!actionKey) return;
                fetch(`/P2P/Settings/ResolveDiscoveryEndpoint?actionKey=${encodeURIComponent(actionKey)}`, {
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => r.json())
                    .then(data => {
                        if (!data || !data.endpoint) return;
                        // One endpoint per line (the server splits on newlines); no duplicates
                        const entries = targetTextArea.value.split(/\r?\n/).map(v => v.trim()).filter(v => v.length > 0);
                        if (!entries.includes(data.endpoint)) {
                            entries.push(data.endpoint);
                            targetTextArea.value = entries.join('\n');
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
                btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin"></i> Saving...';
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
                    // The response is the whole view: take what's inside its wrapper, so wrappers don't nest
                    const doc = new DOMParser().parseFromString(html, 'text/html');
                    const fresh = doc.getElementById(WRAPPER_ID);
                    root.innerHTML = fresh ? fresh.innerHTML : html;

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