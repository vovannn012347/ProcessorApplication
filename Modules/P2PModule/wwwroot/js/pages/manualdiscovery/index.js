/**
 * manual-discovery.js (P2P Manual Discovery Page Orchestration Engine)
 * Manages atomic injection forms and logs transaction returns inside container lifecycles.
 */
(function () {
    const SCRIPT_ID = "p2p-manual-discovery";
    const WRAPPER_ID = "p2p-discovery-wrapper";

    root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Context initialization runtime entry point
         * @param {HTMLElement} el - Element boundary tracking node
         */
        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Registering out-of-band discovery input handlers.`);
            this.attachFormListener(root);
        },

        /**
         * Scopes submission listeners strictly inside wrapper nodes
         */
        attachFormListener: function (root) {
            const form = root.querySelector('#manualDiscoveryForm');
            if (!form) return;

            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleHandshakeExecution(root, form);
            };
        },

        /**
         * Dispatches asynchronous challenge parameters to the back-end controller layer
         */
        handleHandshakeExecution: function (root, form) {
            const btn = root.querySelector('#btnTriggerHandshake');
            const url = form.action;
            const formData = new FormData(form);

            var originalHtml = "";
            if (btn) {
                originalHtml = btn.innerHTML;
                btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin p2p-mr-2"></i> Verifying Cryptographic Proofs...';
                btn.disabled = true;
            }

            fetch(url, {
                method: 'POST',
                body: formData,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(response => {
                    if (!response.ok) throw new Error(`Bootstrap handshake endpoint faulted with status code: ${response.status}`);
                    return response.json();
                })
                .then(data => {
                    alert(data.message);
                    if (data.success) {
                        form.reset();
                    }
                })
                .catch(error => {
                    console.error(`[${SCRIPT_ID}] Cryptographic verification attempt dropped:`, error);
                    alert('A severe networking exception occurred while dispatching verification envelopes.');
                })
                .finally(() => {
                    if (btn) {
                        btn.innerHTML = originalHtml;
                        btn.disabled = false;
                    }
                });
        },

        /**
         * Resource disposal unbinding trigger hook
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Stripping active manual discovery reference dependencies.`);
        }
    };

    // Auto-launch handling hook context verification loop
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }
})();