/**
 * overview.js (P2P Module Mesh Monitor Engine)
 * Controls background discrete polling lifecycles inside isolated workspace layers.
 */
(function () {
    const SCRIPT_ID = "p2p-mesh-overview";
    const WRAPPER_ID = "p2p-mesh-wrapper";
    const POLL_INTERVAL_MS = 60000; // Refreshes once per minute

    let root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        pollTimer: null,
        activeAbortController: null,

        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Mounting mesh overview tracking loop triggers (60s cycle).`);
            this.bindEvents(root);
            this.startPolling(root);
        },

        bindEvents: function (rootElement) {
            const btn = rootElement.querySelector('#btn-force-refresh-mesh');
            if (btn) {
                btn.onclick = (e) => {
                    e.preventDefault();
                    this.executeSync(rootElement);
                };
            }

            const closeConsoleBtn = rootElement.querySelector('#btn-close-console');
            if (closeConsoleBtn) {
                closeConsoleBtn.onclick = () => {
                    const consolePanel = rootElement.querySelector('#console-expander-panel');
                    if (consolePanel) consolePanel.classList.add('p2p-hidden');

                    if (this.activeAbortController) {
                        this.activeAbortController.abort();
                        this.activeAbortController = null;
                    }
                };
            }

            const unifiedForm = rootElement.querySelector('#form-unified-discovery');
            if (unifiedForm) {
                unifiedForm.onsubmit = (e) => {
                    e.preventDefault();
                    this.submitDiscoveryFormStream(unifiedForm, e.submitter);
                };
            }
        },

        submitDiscoveryFormStream: function (formElement, clickedButton) {
            if (!clickedButton) return;

            if (this.activeAbortController) {
                this.activeAbortController.abort();
            }
            this.activeAbortController = new AbortController();

            const consolePanel = root.querySelector('#console-expander-panel');
            const outputArea = root.querySelector('#mesh-processing-output-area');
            const textSource = formElement.querySelector('#p2p-batch-input-source');

            if (consolePanel) consolePanel.classList.remove('p2p-hidden');
            if (outputArea) outputArea.value = "Acquiring connection request pipeline...\n";

            if (!textSource || !textSource.value.trim()) {
                if (outputArea) outputArea.value += "[ABORTED] Input console was empty. Provide entry target strings.\n";
                return;
            }

            const targetAction = clickedButton.getAttribute('data-action');
            const targetParamName = clickedButton.getAttribute('data-param');
            const actionUrl = `/P2P/MeshControl/${targetAction}`;

            const sendBody = new FormData();
            sendBody.append(targetParamName, textSource.value.trim());

            const tokenElement = formElement.querySelector('input[name="__RequestVerificationToken"]');
            if (tokenElement) {
                sendBody.append('__RequestVerificationToken', tokenElement.value);
            }

            fetch(actionUrl, {
                method: 'POST',
                body: sendBody,
                signal: this.activeAbortController.signal,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(async response => {
                    if (!response.ok) throw new Error(`HTTP network error: ${response.status}`);

                    const reader = response.body.getReader();
                    const decoder = new TextDecoder();
                    let buffer = '';

                    while (true) {
                        const { value, done } = await reader.read();
                        if (done) break;

                        buffer += decoder.decode(value, { stream: true });
                        const lines = buffer.split('\n');
                        buffer = lines.pop();

                        for (const line of lines) {
                            if (line.trim().startsWith('data:')) {
                                const message = line.replace('data:', '').trim();
                                if (outputArea) {
                                    outputArea.value += message + "\n";
                                    outputArea.scrollTop = outputArea.scrollHeight;
                                }

                                if (message.includes("| [DONE]")) {
                                    if (outputArea) outputArea.value += "\n[COMPLETED] Pipeline closed cleanly.\n";
                                    this.executeSync(root, true);
                                }
                            }
                        }
                    }
                })
                .catch(err => {
                    if (err.name === 'AbortError') {
                        if (outputArea) outputArea.value += "\n[ABORTED] Task canceled because user collapsed panel or executed a new action.\n";
                    } else {
                        console.error(`[${SCRIPT_ID}] Ingestion stream crashed:`, err);
                        if (outputArea) outputArea.value += `\n[ERROR] Pipeline exception occurred: ${err.message}\n`;
                    }
                });
        },

        startPolling: function (rootElement) {
            this.stopPolling();
            this.pollTimer = setInterval(() => {
                this.executeSync(rootElement, true);
            }, POLL_INTERVAL_MS);
        },

        stopPolling: function () {
            if (this.pollTimer) {
                clearInterval(this.pollTimer);
                this.pollTimer = null;
            }
            if (this.activeAbortController) {
                this.activeAbortController.abort();
                this.activeAbortController = null;
            }
        },

        executeSync: function (rootElement, isBackground) {
            const icon = rootElement.querySelector('#refresh-icon');
            const targetTableBody = rootElement.querySelector('#p2p-node-table-body');
            const fetchUrl = window.location.pathname;

            if (!isBackground && icon) {
                icon.classList.add('fa-spin');
            }

            fetch(fetchUrl, {
                method: 'GET',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(response => {
                    if (!response.ok) throw new Error(`HTTP tracking fault status: ${response.status}`);
                    return response.text();
                })
                .then(htmlFragment => {
                    if (targetTableBody) {
                        targetTableBody.innerHTML = htmlFragment;
                    }
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Maintenance cycle drop:`, err);
                })
                .finally(() => {
                    if (!isBackground && icon) {
                        icon.classList.remove('fa-spin');
                    }
                });
        },

        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Evicting polling runtimes.`);
            this.stopPolling();
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }
})();