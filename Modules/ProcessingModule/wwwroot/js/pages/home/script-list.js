/**
 * home-scriptlist.js (Processor Module)
 * Manages the Medical Analysis Library inventory and script execution prep.
 */
(function () {
    const SCRIPT_ID = "processor-scriptlist";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        // Internal state (scoped per instance)
        selectedScriptIds: [],

        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            const root = el || document.getElementById('script-page-wrapper');
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing script library...`);

            // Reset state if re-initializing
            this.selectedScriptIds = [];

            // Ensure UI reflects current (empty) selection
            this.updateSelectionUI(root);
        },

        toggleScriptId: function (id, root) {
            const index = this.selectedScriptIds.indexOf(id);
            if (index === -1) {
                this.selectedScriptIds.push(id);
            } else {
                this.selectedScriptIds.splice(index, 1);
            }
            this.updateSelectionUI(root);
        },

        toggleSelectionRow: function (id, root) {
            const chk = root.querySelector('#chk-' + id);
            if (chk && !chk.disabled) {
                chk.checked = !chk.checked;
                this.toggleScriptId(id, root);
            }
        },

        updateSelectionUI: function (root) {
            const btn = root.querySelector('#launchBtn');
            if (!btn) return;

            // Visual feedback for selection
            if (this.selectedScriptIds.length > 0) {
                btn.classList.replace('pm-bg-indigo-600', 'pm-bg-indigo-700');
            } else {
                btn.classList.replace('pm-bg-indigo-700', 'pm-bg-indigo-600');
            }
        },

        submitSelection: function (root) {
            if (this.selectedScriptIds.length === 0) {
                alert("Please select at least one script from the library to proceed.");
                return;
            }

            const form = root.querySelector('#launchForm');
            const hiddenInput = root.querySelector('#selectedScripts');

            if (form && hiddenInput) {
                hiddenInput.value = this.selectedScriptIds.join(',');
                form.submit();
            }
        },

        toggleDetails: async function (id, scriptIdentifier, root) {
            const row = root.querySelector('#details-' + id);
            const content = root.querySelector('#content-' + id);
            const icon = root.querySelector('#icon-' + id);

            if (!row || !content || !icon) return;

            if (row.classList.contains('pm-hidden')) {
                row.classList.remove('pm-hidden');
                icon.classList.add('pm-text-indigo-500');

                try {
                    const response = await fetch(`/processing/Home/GetScriptDetails?scriptIdentifier=${scriptIdentifier}`, {
                        headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });
                    if (!response.ok) throw new Error();
                    content.innerHTML = await response.text();
                } catch (err) {
                    content.innerHTML = '<span class="pm-text-[9px] pm-text-red-400"><i class="fa-solid fa-circle-exclamation mr-1"></i> Error loading details.</span>';
                }
            } else {
                row.classList.add('pm-hidden');
                icon.classList.remove('pm-text-indigo-500');
            }
        },

        runReindex: function (btn) {
            if (!btn) return;

            // Find the icon relative to the clicked button
            const icon = btn.querySelector('#reindexIcon');
            if (icon) {
                icon.classList.add('fa-spin');
            }

            // Disable button to prevent double-clicks during redirection
            btn.disabled = true;
            btn.classList.add('mm-opacity-50');

            console.log("[Processor] Initiating library reindex...");
            window.location.href = '/Processing/Admin/ReindexLogs';
        },

        /**
         * THE UNLOADING HOOK
         * @param {HTMLElement} el - The element being removed
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Disposing library resources.`);
            // Empty signature for future cleanup
        }
    };

    // SELF-LAUNCHING BLOCK
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    // Expose as alias for inline onclicks
    window.ProcessorLibrary = window.ModuleRegistry[SCRIPT_ID];

    //# sourceURL=js/modules/processor/home-scriptlist.js
})();