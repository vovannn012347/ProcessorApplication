/**
 * file-exchange/index.js (Live Swarm Transfers Telemetry & Bitfield Inspector)
 * Polls active downloads every 3s, manages task actions (Pause/Resume/Cancel),
 * and dynamically renders real-time chunk verification bitfields and WebRTC lane states.
 */
(function () {
    const SCRIPT_ID = "file-exchange";
    const WRAPPER_ID = "file-exchange-wrapper";
    const CONTROLLER_PATH = "FileTr/FileExchange";

    let root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        pollTimer: null,

        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing File Exchange Telemetry.`);
            this.bindEvents(root);
            this.startTelemetryPolling(root);
        },

        bindEvents: function (rootElement) {
            const token = rootElement.querySelector('input[name="__RequestVerificationToken"]')?.value;
            const telemetryContainer = rootElement.querySelector('#exchange-telemetry-container');
            const btnRefresh = rootElement.querySelector('#btn-refresh-exchange');

            const bitfieldModal = rootElement.querySelector('#bitfield-modal') || document.getElementById('bitfield-modal');
            const bitfieldContent = bitfieldModal?.querySelector('#bitfield-modal-content');
            const btnCloseBitfield = bitfieldModal?.querySelector('#btn-close-bitfield-modal');

            if (btnCloseBitfield && bitfieldModal) {
                btnCloseBitfield.onclick = () => {
                    bitfieldModal.classList.add('hidden');
                    if (bitfieldContent) bitfieldContent.innerHTML = '';
                };
            }

            if (btnRefresh) {
                btnRefresh.onclick = () => this.refreshTelemetry(telemetryContainer);
            }

            // Delegated Task Actions (Bitfield / Pause / Resume / Cancel)
            if (telemetryContainer) {
                telemetryContainer.onclick = (e) => {
                    const btn = e.target.closest('button[data-action]');
                    if (!btn) return;

                    const action = btn.dataset.action;
                    const taskId = btn.dataset.task;

                    if (action === 'view-bitfield') {
                        const url = `${window.location.origin}/${CONTROLLER_PATH}/GetBitfieldState?taskId=${encodeURIComponent(taskId)}`;
                        fetch(url, {
                            headers: { 'X-Requested-With': 'XMLHttpRequest' }
                        })
                            .then(res => res.text())
                            .then(html => {
                                if (bitfieldContent && bitfieldModal) {
                                    bitfieldContent.innerHTML = html;
                                    bitfieldModal.classList.remove('hidden');
                                }
                            })
                            .catch(err => console.error(`[${SCRIPT_ID}] Bitfield state error:`, err));
                    } else if (action === 'pause-task') {
                        this.executePostAction('Pause', { taskId: taskId }, token, null, () => {
                            this.refreshTelemetry(telemetryContainer);
                        });
                    } else if (action === 'resume-task') {
                        this.executePostAction('Resume', { taskId: taskId }, token, null, () => {
                            this.refreshTelemetry(telemetryContainer);
                        });
                    } else if (action === 'cancel-task') {
                        this.executePostAction('Cancel', { taskId: taskId }, token, 'Cancel and abort this swarm transfer?', () => {
                            this.refreshTelemetry(telemetryContainer);
                        });
                    }
                };
            }
        },

        startTelemetryPolling: function (rootElement) {
            const telemetryContainer = rootElement.querySelector('#exchange-telemetry-container');
            if (this.pollTimer) clearInterval(this.pollTimer);

            // Poll active transfers every 3 seconds
            this.pollTimer = setInterval(() => {
                if (!document.getElementById(WRAPPER_ID)) {
                    this.dispose();
                    return;
                }
                this.refreshTelemetry(telemetryContainer);
            }, 3000);
        },

        refreshTelemetry: function (container) {
            if (!container) return;
            const url = `${window.location.origin}/${CONTROLLER_PATH}/GetTransfersTelemetry`;

            fetch(url, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.text())
                .then(html => {
                    container.innerHTML = html;
                })
                .catch(err => console.warn(`[${SCRIPT_ID}] Polling error:`, err));
        },

        executePostAction: function (actionName, payload, verificationToken, confirmMsg = null, callback = null) {
            if (confirmMsg && !confirm(confirmMsg)) return;

            const url = `${window.location.origin}/${CONTROLLER_PATH}/${actionName}`;
            const params = new URLSearchParams();

            for (const key in payload) params.append(key, payload[key]);
            if (verificationToken) params.append('__RequestVerificationToken', verificationToken);

            fetch(url, {
                method: 'POST',
                body: params,
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
                .then(res => res.json())
                .then(data => {
                    if (callback) callback(data);
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Command ${actionName} error:`, err));
        },

        dispose: function () {
            if (this.pollTimer) {
                clearInterval(this.pollTimer);
                this.pollTimer = null;
            }
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) window.ModuleRegistry[SCRIPT_ID].init(hook);
})();