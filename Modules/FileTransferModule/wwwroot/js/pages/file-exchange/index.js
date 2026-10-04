/**
 * file-exchange/index.js (Live Swarm Transfers Telemetry & Bitfield Inspector)
 * Polls active downloads every 3s, manages task actions (Pause/Resume/Cancel),
 * and renders the bitfield/peer-lanes modal. data-task carries TaskId (not ContentHash -
 * those are deliberately different ids, see SwarmTransferTaskState remarks).
 */
(function () {
    const SCRIPT_ID = "ftt-exchange";
    const WRAPPER_ID = "file-exchange-wrapper";
    const CONTROLLER_PATH = "FileTr/FileExchange";
    const POLL_INTERVAL_MS = 3000;

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

        getAntiForgeryToken: function (rootElement) {
            const input = rootElement.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : null;
        },

        bindEvents: function (rootElement) {
            const telemetryContainer = rootElement.querySelector('#exchange-telemetry-container');
            const btnRefresh = rootElement.querySelector('#btn-refresh-exchange');

            const bitfieldModal = document.getElementById('bitfield-modal');
            const bitfieldContent = bitfieldModal?.querySelector('#bitfield-modal-content');
            const btnCloseBitfield = bitfieldModal?.querySelector('#btn-close-bitfield-modal');

            if (btnCloseBitfield && bitfieldModal) {
                btnCloseBitfield.onclick = () => {
                    bitfieldModal.classList.add('ftt-hidden');
                    if (bitfieldContent) bitfieldContent.innerHTML = '';
                };
            }

            if (btnRefresh) {
                btnRefresh.onclick = () => this.refreshTelemetry(telemetryContainer);
            }

            if (telemetryContainer) {
                telemetryContainer.onclick = (e) => {
                    const btn = e.target.closest('button[data-action]');
                    if (!btn) return;

                    const action = btn.dataset.action;
                    const taskId = btn.dataset.task;

                    if (action === 'view-bitfield') {
                        fetch(`/${CONTROLLER_PATH}/GetBitfieldState?taskId=${encodeURIComponent(taskId)}`, {
                            headers: { 'X-Requested-With': 'XMLHttpRequest' }
                        })
                            .then(res => res.text())
                            .then(html => {
                                if (bitfieldContent && bitfieldModal) {
                                    bitfieldContent.innerHTML = html;
                                    bitfieldModal.classList.remove('ftt-hidden');
                                }
                            })
                            .catch(err => console.error(`[${SCRIPT_ID}] Bitfield state error:`, err));
                    } else if (action === 'pause-task') {
                        this.executePostAction(rootElement, 'Pause', { taskId }, null, () => this.refreshTelemetry(telemetryContainer));
                    } else if (action === 'resume-task') {
                        this.executePostAction(rootElement, 'Resume', { taskId }, null, () => this.refreshTelemetry(telemetryContainer));
                    } else if (action === 'cancel-task') {
                        this.executePostAction(rootElement, 'Cancel', { taskId }, 'Cancel and abort this swarm transfer?', () => this.refreshTelemetry(telemetryContainer));
                    }
                };
            }
        },

        startTelemetryPolling: function (rootElement) {
            if (this.pollTimer) clearInterval(this.pollTimer);

            this.pollTimer = setInterval(() => {
                if (!document.getElementById(WRAPPER_ID)) {
                    this.dispose();
                    return;
                }
                this.refreshTelemetry(rootElement.querySelector('#exchange-telemetry-container'));
            }, POLL_INTERVAL_MS);
        },

        refreshTelemetry: function (container) {
            if (!container) return;
            fetch(`/${CONTROLLER_PATH}/GetTransfersTelemetry`, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(res => res.text())
                .then(html => { container.innerHTML = html; })
                .catch(err => console.warn(`[${SCRIPT_ID}] Polling error:`, err));
        },

        executePostAction: function (rootElement, actionName, payload, confirmMsg, callback) {
            if (confirmMsg && !confirm(confirmMsg)) return;

            const token = this.getAntiForgeryToken(rootElement);
            const body = new FormData();
            for (const key in payload) body.append(key, payload[key]);
            if (token) body.append('__RequestVerificationToken', token);

            fetch(`/${CONTROLLER_PATH}/${actionName}`, {
                method: 'POST', body: body, headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.json())
                .then(data => { if (callback) callback(data); })
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
