/**
 * user profile
 * Handles profile actions like connectivity checks and scoped form behavior.
 */
(function () {
    const SCRIPT_ID = "user-profile";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The form element hooked via data-script-id
         */
        init: function (el) {
            // Fallback for direct loads if el isn't passed
            const root = el || document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing...`);
            this.attachConnectivityCheck(root);

            // Example of a resource that would need disposal (e.g. a chart resize listener)
            // this.boundResize = this.onResize.bind(this);
            // window.addEventListener('resize', this.boundResize);
        },

        /**
         * Scoped Connectivity Check logic
         */
        attachConnectivityCheck: function (root) {
            const checkBtn = root.querySelector('#check-connectivity-btn');
            const resultP = root.querySelector('#connectivity-result');

            if (!checkBtn || !resultP) return;

            // Using onclick to ensure we don't stack listeners during AJAX re-inits
            checkBtn.onclick = (e) => {
                e.preventDefault();

                checkBtn.disabled = true;
                const originalHtml = checkBtn.innerHTML;
                checkBtn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin mm-mr-2"></i> Checking...';

                resultP.classList.add('mm-hidden');
                // Reset classes while keeping core styles
                resultP.className = 'mm-text-xs mm-mt-2 mm-text-center';

                fetch('/Main/Profile/CheckConnectivity', {
                    method: 'POST',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                })
                    .then(r => r.json())
                    .then(data => {
                        resultP.textContent = data.message;
                        resultP.classList.remove('mm-hidden');

                        if (data.success) {
                            resultP.classList.add('mm-text-green-700', 'mm-font-bold');
                        } else {
                            resultP.classList.add('mm-text-red-600');
                        }
                    })
                    .catch(err => {
                        resultP.textContent = "Error: Failed to contact server.";
                        resultP.classList.remove('mm-hidden');
                        resultP.classList.add('mm-text-red-600');
                        console.error(`[${SCRIPT_ID}] Connectivity check failed:`, err);
                    })
                    .finally(() => {
                        checkBtn.disabled = false;
                        checkBtn.innerHTML = originalHtml;
                    });
            };
        },

        /**
         * THE UNLOADING HOOK
         * @param {HTMLElement} el - The element being removed
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Disposing...`);

            // Commented sample of resource cleanup:
            /*
            if (this.boundResize) {
                window.removeEventListener('resize', this.boundResize);
            }
            */
        }
    };

    // SELF-LAUNCHING BLOCK: Handles the first load or direct navigation
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    //# sourceURL=js/modules/main/profile.js
})();