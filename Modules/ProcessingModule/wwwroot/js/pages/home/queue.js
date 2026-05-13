/**
 * home-queue.js (Processor Module)
 * Manages the Processing History table, task controls, and sub-job artifact extraction.
 */
(function () {
    const SCRIPT_ID = "processor-queue";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            const root = el || document.getElementById('queue-page-wrapper');
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing history orchestrator...`);
        },

        /**
         * Toggles the visibility of the sub-job (steps) row
         */
        toggleRow: function (id, root) {
            const row = root.querySelector(`#${id}`);
            if (row) row.classList.toggle('pm-hidden');
        },

        /**
         * Sends control signals (Pause/Stop/Resume) to the backend
         */
        controlTask: async function (id, action, root) {
            // Use a custom UI confirm if possible, otherwise standard confirm
            if (!confirm(`Are you sure you want to ${action} this task?`)) return;

            try {
                const response = await fetch(`/processing/Home/${action}Job?id=${id}`, {
                    method: 'POST',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                });

                if (response.ok) {
                    // Instead of location.reload() which breaks the modular flow,
                    // we re-trigger the load of the current view.
                    if (window.loadContent) {
                        window.loadContent('/Processing/Home/Queue');
                    } else {
                        location.reload();
                    }
                }
            } catch (err) {
                console.error(`[${SCRIPT_ID}] Control failed:`, err);
            }
        },

        /**
         * Lazy-loads sub-job details/artifacts
         */
        loadDetails: async function (parentId, subId, btn, root) {
            const container = root.querySelector(`#details-${subId}`);
            const icon = root.querySelector(`#icon-${subId}`);

            if (!container || !icon) return;

            if (container.classList.contains('pm-hidden')) {
                container.classList.remove('pm-hidden');
                icon.classList.replace('fa-chevron-right', 'fa-chevron-down');

                // Only fetch if we still see the loader
                if (container.querySelector('.loader')) {
                    try {
                        const response = await fetch(`/processing/Home/GetSubJobDetails?subJobId=${subId}`, {
                            headers: { 'X-Requested-With': 'XMLHttpRequest' }
                        });
                        container.innerHTML = await response.text();
                    } catch (err) {
                        container.innerHTML = '<div class="pm-p-4 pm-text-[9px] pm-text-red-500">Failed to retrieve artifacts.</div>';
                    }
                }
            } else {
                container.classList.add('pm-hidden');
                icon.classList.replace('fa-chevron-down', 'fa-chevron-right');
            }
        },

        /**
         * THE UNLOADING HOOK
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Disposing queue resources.`);
        }
    };

    // SELF-LAUNCHING BLOCK
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    // Alias for inline onclick handlers
    window.ProcessorQueue = window.ModuleRegistry[SCRIPT_ID];

    //# sourceURL=js/modules/processor/home-queue.js
})();