/**
 * admin-reindex.js (Processor Module)
 * Manages the real-time SSE stream for library reindexing.
 */
(function () {
    const SCRIPT_ID = "processor-reindex-logs";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        eventSource: null,

        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The wrapper element hooked via data-script-id
         */
        init: function (el) {
            const root = el || document.getElementById('reindex-console-wrapper');
            if (!root) return;

            const output = root.querySelector('#consoleOutput');
            const statusParagraph = root.querySelector('#statusText');

            console.log(`[${SCRIPT_ID}] Opening SSE stream for indexing...`);

            // 1. Ensure any previous stream is closed
            this.dispose();

            // 2. Establish Server-Sent Events connection
            this.eventSource = new EventSource('/Processing/Admin/ConsecutiveReindex');

            this.eventSource.onmessage = (event) => {
                if (!statusParagraph || !output) return;

                if (event.data === "[DONE]") {
                    statusParagraph.innerText = "Indexing Complete";
                    statusParagraph.classList.remove('pm-animate-pulse', 'pm-text-indigo-600');
                    statusParagraph.classList.add('pm-text-green-600');
                    output.value += "\n\r>>> REINDEXING PROCESS FINISHED SUCCESSFULLY.";
                    this.dispose(); // Close naturally when finished
                } else {
                    statusParagraph.innerText = "Processing...";
                    output.value += event.data + "\n";
                    output.scrollTop = output.scrollHeight; // Auto-scroll
                }
            };

            this.eventSource.onerror = (err) => {
                if (!statusParagraph || !output) return;

                statusParagraph.innerText = "Connection Lost / Error";
                statusParagraph.classList.add('pm-text-red-600');
                output.value += "\n\r[CRITICAL ERROR] The stream was interrupted. Check server logs.";
                this.dispose();
            };
        },

        /**
         * THE UNLOADING HOOK
         * Critical for SSE to prevent background memory leaks.
         */
        dispose: function (el) {
            if (this.eventSource) {
                console.log(`[${SCRIPT_ID}] Closing EventSource connection.`);
                this.eventSource.close();
                this.eventSource = null;
            }
        }
    };

    // SELF-LAUNCHING BLOCK
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    //# sourceURL=js/modules/processor/admin-reindex.js
})();