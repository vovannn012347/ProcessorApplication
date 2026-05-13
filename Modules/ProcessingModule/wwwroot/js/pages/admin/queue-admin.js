/**
 * admin-queue.js (Processor Module)
 * Handles global sequence monitoring, administrative overrides, and data purging.
 */
(function () {
    const SCRIPT_ID = "processor-admin-queue";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            const root = el || document.getElementById('admin-queue-wrapper');
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Administrative monitoring initialized.`);
        },

        toggleRow: function (id, root) {
            const row = root.querySelector(`#${id}`);
            if (row) row.classList.toggle('pm-hidden');
        },

        controlTask: async function (id, action, root) {
            if (!confirm(`ADMIN OVERRIDE: Are you sure you want to ${action} this global task?`)) return;

            try {
                const response = await fetch(`/processing/Home/${action}Job?id=${id}`, {
                    method: 'POST',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                });

                if (response.ok) {
                    this.refreshView();
                }
            } catch (err) {
                console.error(`[${SCRIPT_ID}] Admin control failed:`, err);
            }
        },

        /**
         * CRITICAL: Handles permanent deletion of task data and files
         */
        purgeJob: async function (id, root) {
            if (!confirm("CRITICAL ACTION: This will permanently delete all task data from the database and the physical disk. This cannot be undone. Proceed?")) return;

            // Extract the Anti-Forgery Token from the layout's hidden input
            const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;

            try {
                const response = await fetch(`/processing/Admin/PurgeJob?id=${id}`, {
                    method: 'POST',
                    headers: {
                        'X-Requested-With': 'XMLHttpRequest',
                        'RequestVerificationToken': token
                    }
                });

                if (response.ok) {
                    this.refreshView();
                } else {
                    alert("Error: Unauthorized or failed to purge the job.");
                }
            } catch (err) {
                console.error(`[${SCRIPT_ID}] Purge failed:`, err);
            }
        },

        loadDetails: async function (parentId, subId, btn, root) {
            const container = root.querySelector(`#details-${subId}`);
            const icon = root.querySelector(`#icon-${subId}`);

            if (!container || !icon) return;

            if (container.classList.contains('pm-hidden')) {
                container.classList.remove('pm-hidden');
                icon.classList.replace('fa-chevron-right', 'fa-chevron-down');

                if (container.querySelector('.pm-loader')) {
                    try {
                        const response = await fetch(`/processing/Home/GetSubJobDetails?subJobId=${subId}`, {
                            headers: { 'X-Requested-With': 'XMLHttpRequest' }
                        });
                        container.innerHTML = await response.text();
                    } catch (err) {
                        container.innerHTML = '<div class="pm-p-4 pm-text-red-500">Failed to load artifacts.</div>';
                    }
                }
            } else {
                container.classList.add('pm-hidden');
                icon.classList.replace('fa-chevron-down', 'fa-chevron-right');
            }
        },

        runReindex: function (root) {
            const icon = root.querySelector('#reindexIcon');
            if (icon) icon.classList.add('fa-spin');
            window.location.href = '/Processing/Admin/ReindexLogs';
        },

        /**
         * Refreshes the current view without a full page reload
         */
        refreshView: function () {
            if (window.loadContent) {
                // Determine current page from URL params if needed
                const urlParams = new URLSearchParams(window.location.search);
                const page = urlParams.get('page') || '1';
                window.loadContent(`/processing/Admin/QueueAdmin?page=${page}`);
            } else {
                location.reload();
            }
        },

        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Releasing administrative hooks.`);
        }
    };

    // SELF-LAUNCHING BLOCK
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    window.ProcessorAdmin = window.ModuleRegistry[SCRIPT_ID];

    //# sourceURL=js/modules/processor/admin-queue.js
})();