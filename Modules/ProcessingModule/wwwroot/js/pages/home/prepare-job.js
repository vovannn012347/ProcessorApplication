/**
 * home-preparejob.js (Processor Module)
 * Manages job configuration, file selection previews, and orchestration initialization.
 */
(function () {
    const SCRIPT_ID = "processor-preparejob";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            const root = el || document.getElementById('prepare-job-wrapper');
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing job configuration...`);
            this.attachFormListener(root);
        },

        /**
         * Scoped form listener for submission state
         */
        attachFormListener: function (root) {
            const form = root.querySelector('#jobForm');
            const btn = root.querySelector('#submitBtn');
            if (!form || !btn) return;

            form.onsubmit = () => {
                btn.disabled = true;
                btn.innerText = 'UPLOADING AND INITIALIZING...';
                btn.classList.add('pm-opacity-50', 'pm-cursor-not-allowed');
                // Return true to allow standard form submission (multipart/form-data)
                return true;
            };
        },

        /**
         * Handles the dynamic preview of selected files or folders
         */
        handleFileSelect: function (input, key, root) {
            const preview = root.querySelector('#preview_' + key);
            if (!preview) return;

            const list = preview.querySelector('ul');
            if (!list) return;

            list.innerHTML = '';

            if (input.files.length > 0) {
                preview.classList.remove('pm-hidden');

                // Show up to 50 files for performance
                Array.from(input.files).slice(0, 50).forEach(file => {
                    const li = document.createElement('li');
                    li.className = 'pm-truncate pm-py-0.5 pm-border-b pm-border-slate-100 last:pm-border-0';
                    li.textContent = file.name;
                    list.appendChild(li);
                });

                if (input.files.length > 50) {
                    const li = document.createElement('li');
                    li.className = 'pm-italic pm-text-slate-400 pm-pt-1';
                    li.textContent = `...and ${input.files.length - 50} more files`;
                    list.appendChild(li);
                }
            } else {
                preview.classList.add('pm-hidden');
            }
        },

        /**
         * THE UNLOADING HOOK
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Disposing preparation resources.`);
        }
    };

    // SELF-LAUNCHING BLOCK for first-time injection
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    // Alias for inline Razor event handlers
    window.ProcessorPrepare = window.ModuleRegistry[SCRIPT_ID];

    //# sourceURL=js/modules/processor/home-preparejob.js
})();