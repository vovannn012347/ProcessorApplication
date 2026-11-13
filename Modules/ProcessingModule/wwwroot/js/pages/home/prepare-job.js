/**
 * home-preparejob.js (Processor Module)
 * Manages job configuration, file selection previews, and orchestration initialization.
 */
(function () {
    const SCRIPT_ID = "processor-preparejob";

    let root = {}; // local to this script (was an implicit global shared by all page scripts)

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            root = el || document.getElementById('prepare-job-wrapper');
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing job configuration...`);
            this.attachFormListener(root);
        },

        /**
         * Scoped form listener for submission state
         */
        attachFormListener: function () {
            const form = root.querySelector('#jobForm');
            const btn = root.querySelector('#submitBtn');
            if (!form || !btn) return;

            // File inputs carry data-preview-key; show the chosen file names under them
            form.onchange = (e) => {
                const input = e.target;
                if (input && input.type === 'file' && input.dataset.previewKey) {
                    this.handleFileSelect(input, input.dataset.previewKey);
                }
            };

            form.onsubmit = () => {
                btn.disabled = true;
                btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin"></i> Uploading and starting...';
                
                // Return true to allow standard form submission (multipart/form-data)
                return true;
            };
        },

        /**
         * Handles the dynamic preview of selected files or folders
         */
        handleFileSelect: function (input, key) {
            const preview = document.getElementById('preview_' + key); // keys may hold characters a CSS selector can't take
            if (!preview) return;

            const list = preview.querySelector('ul');
            if (!list) return;

            list.innerHTML = '';

            if (input.files.length > 0) {
                preview.classList.remove('is-hidden');

                // Show up to 50 files for performance
                Array.from(input.files).slice(0, 50).forEach(file => {
                    const li = document.createElement('li');
                    li.className = 'wrap';
                    li.textContent = file.name;
                    list.appendChild(li);
                });

                if (input.files.length > 50) {
                    const li = document.createElement('li');
                    li.className = 'aux';
                    li.textContent = `...and ${input.files.length - 50} more files`;
                    list.appendChild(li);
                }
            } else {
                preview.classList.add('is-hidden');
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