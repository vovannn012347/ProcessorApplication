/**
 * file-archive/index.js (File Archive & Local Seeding Page Handler)
 * Follows the ModuleRegistry pattern with live AJAX partial reloading, modal state controls,
 * and parameterized POST operations with anti-forgery tokens.
 */
(function () {
    const SCRIPT_ID = "file-archive";
    const WRAPPER_ID = "file-archive-wrapper";
    const CONTROLLER_PATH = "FileTr/FileArchive";

    let root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        pollTimer: null,

        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing File Archive Controller.`);
            this.bindEvents(root);
        },

        bindEvents: function (rootElement) {
            const token = rootElement.querySelector('input[name="__RequestVerificationToken"]')?.value;
            const modal = rootElement.querySelector('#ingest-file-modal') || document.getElementById('ingest-file-modal');
            const formIngest = rootElement.querySelector('#form-ingest-file') || document.getElementById('form-ingest-file');
            const tableContainer = rootElement.querySelector('#archive-table-container');

            // Open Ingest Modal
            const btnOpen = rootElement.querySelector('#btn-open-ingest-modal');
            if (btnOpen && modal) {
                btnOpen.onclick = () => {
                    modal.classList.remove('hidden');
                };
            }

            // Close Ingest Modal Buttons
            const btnClose = modal?.querySelector('#btn-close-ingest-modal');
            const btnCancel = modal?.querySelector('#btn-cancel-ingest');
            const closeModal = () => {
                if (modal) {
                    modal.classList.add('hidden');
                    formIngest?.reset();
                }
            };
            if (btnClose) btnClose.onclick = closeModal;
            if (btnCancel) btnCancel.onclick = closeModal;

            // Manual Refresh Button
            const btnRefresh = rootElement.querySelector('#btn-refresh-archive');
            if (btnRefresh) {
                btnRefresh.onclick = () => this.refreshTrackedList(tableContainer);
            }

            // Handle Ingestion Form Submit (Multipart / AJAX)
            if (formIngest) {
                formIngest.onsubmit = async (e) => {
                    e.preventDefault();
                    const spinner = formIngest.querySelector('#ingest-spinner');
                    const submitBtn = formIngest.querySelector('#btn-submit-ingest');

                    if (spinner) spinner.classList.remove('hidden');
                    if (submitBtn) submitBtn.disabled = true;

                    const formData = new FormData(formIngest);

                    try {
                        const resp = await fetch(`${window.location.origin}/${CONTROLLER_PATH}/IngestFile`, {
                            method: 'POST',
                            body: formData,
                            headers: {
                                'X-Requested-With': 'XMLHttpRequest'
                            }
                        });

                        const data = await resp.json();
                        if (data.success) {
                            this.showStatus(rootElement, `Successfully ingested and seeding "${data.fileName}".`, true);
                            closeModal();
                            this.refreshTrackedList(tableContainer);
                        } else {
                            this.showStatus(rootElement, data.message || 'File ingestion failed.', false);
                        }
                    } catch (err) {
                        console.error(`[${SCRIPT_ID}] Ingest upload error:`, err);
                        this.showStatus(rootElement, 'Network failure during ingestion.', false);
                    } finally {
                        if (spinner) spinner.classList.add('hidden');
                        if (submitBtn) submitBtn.disabled = false;
                    }
                };
            }

            // Delegated Action Clicks on Table Container
            if (tableContainer) {
                tableContainer.onclick = (e) => {
                    const btn = e.target.closest('button[data-action]');
                    if (!btn) return;

                    const action = btn.dataset.action;
                    const hash = btn.dataset.hash;

                    if (action === 'toggle-visibility') {
                        this.executePostAction('ToggleVisibility', { contentHash: hash }, token, null, () => {
                            this.refreshTrackedList(tableContainer);
                        });
                    } else if (action === 'delete-file') {
                        this.executePostAction('DeleteFile', { contentHash: hash, deletePhysical: false }, token, 'Stop seeding and remove this file from tracking?', () => {
                            this.refreshTrackedList(tableContainer);
                        });
                    }
                };
            }
        },

        refreshTrackedList: function (tableContainer) {
            if (!tableContainer) return;
            const url = `${window.location.origin}/${CONTROLLER_PATH}/GetTrackedList`;

            fetch(url, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.text())
                .then(html => {
                    tableContainer.innerHTML = html;
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Error refreshing table:`, err));
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
                    if (data.message) alert(data.message);
                    if (callback) callback(data);
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Post action ${actionName} failed:`, err));
        },

        showStatus: function (rootElement, message, isSuccess) {
            const box = rootElement.querySelector('#archive-status-message');
            if (!box) return;

            box.textContent = message;
            box.className = isSuccess
                ? 'mb-4 p-3 rounded-lg text-xs font-medium bg-emerald-50 text-emerald-800 border border-emerald-200 block'
                : 'mb-4 p-3 rounded-lg text-xs font-medium bg-red-50 text-red-800 border border-red-200 block';
            box.classList.remove('hidden');

            setTimeout(() => box.classList.add('hidden'), 5000);
        },

        dispose: function () {
            if (this.pollTimer) clearInterval(this.pollTimer);
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) window.ModuleRegistry[SCRIPT_ID].init(hook);
})();