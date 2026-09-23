/**
 * file-catalog/index.js (Mesh Catalog Search & Downloader Dispatcher)
 * Queries remote tracker entries, executes targeted node probing, and handles custom destination downloads.
 */
(function () {
    const SCRIPT_ID = "file-catalog";
    const WRAPPER_ID = "file-catalog-wrapper";
    const CONTROLLER_PATH = "FileTr/FileCatalog";

    let root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        pollTimer: null,

        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing File Catalog Controller.`);
            this.bindEvents(root);
        },

        bindEvents: function (rootElement) {
            const token = rootElement.querySelector('input[name="__RequestVerificationToken"]')?.value;
            const gridContainer = rootElement.querySelector('#catalog-grid-container');
            const formQuery = rootElement.querySelector('#form-query-node');
            const btnRefresh = rootElement.querySelector('#btn-refresh-catalog');

            const downloadModal = rootElement.querySelector('#download-prompt-modal') || document.getElementById('download-prompt-modal');
            const formDownload = rootElement.querySelector('#form-start-download') || document.getElementById('form-start-download');

            // Refresh Entire Catalog
            if (btnRefresh) {
                btnRefresh.onclick = () => this.refreshCatalog(gridContainer);
            }

            // Probe Specific Node Directly
            if (formQuery) {
                formQuery.onsubmit = (e) => {
                    e.preventDefault();
                    const nodeIdInput = formQuery.querySelector('#input-target-node-id');
                    const nodeId = nodeIdInput?.value?.trim();
                    if (!nodeId) return;

                    const url = `${window.location.origin}/${CONTROLLER_PATH}/QueryNode?nodeId=${encodeURIComponent(nodeId)}`;
                    fetch(url, {
                        headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    })
                        .then(res => res.text())
                        .then(html => {
                            if (gridContainer) gridContainer.innerHTML = html;
                            this.showStatus(rootElement, `Queried Node ${nodeId}`, true);
                        })
                        .catch(err => {
                            console.error(`[${SCRIPT_ID}] Node probe error:`, err);
                            this.showStatus(rootElement, `Failed to query node ${nodeId}`, false);
                        });
                };
            }

            // Modal Dismiss Handlers
            const btnCloseDl = downloadModal?.querySelector('#btn-close-download-modal');
            const btnCancelDl = downloadModal?.querySelector('#btn-cancel-download');
            const closeDlModal = () => {
                if (downloadModal) downloadModal.classList.add('hidden');
            };
            if (btnCloseDl) btnCloseDl.onclick = closeDlModal;
            if (btnCancelDl) btnCancelDl.onclick = closeDlModal;

            // Delegated Download Button Click on Grid
            if (gridContainer) {
                gridContainer.onclick = (e) => {
                    const btn = e.target.closest('button[data-action="prompt-download"]');
                    if (!btn || !downloadModal) return;

                    const host = btn.dataset.host;
                    const hash = btn.dataset.hash;
                    const filename = btn.dataset.filename;

                    downloadModal.querySelector('#modal-host-node-id').value = host || '';
                    downloadModal.querySelector('#modal-content-hash').value = hash || '';
                    downloadModal.querySelector('#modal-display-filename').textContent = filename || 'Unknown';
                    downloadModal.querySelector('#modal-input-filename').value = filename || '';

                    const customDir = downloadModal.querySelector('#modal-input-directory');
                    if (customDir) customDir.value = '';

                    downloadModal.classList.remove('hidden');
                };
            }

            // Submit Swarm Download Request
            if (formDownload) {
                formDownload.onsubmit = async (e) => {
                    e.preventDefault();
                    const spinner = formDownload.querySelector('#download-spinner');
                    const confirmBtn = formDownload.querySelector('#btn-confirm-download');

                    if (spinner) spinner.classList.remove('hidden');
                    if (confirmBtn) confirmBtn.disabled = true;

                    const formData = new FormData(formDownload);

                    try {
                        const resp = await fetch(`${window.location.origin}/${CONTROLLER_PATH}/StartDownload`, {
                            method: 'POST',
                            body: formData,
                            headers: {
                                'X-Requested-With': 'XMLHttpRequest'
                            }
                        });

                        const res = await resp.json();
                        if (res.success) {
                            this.showStatus(rootElement, `Swarm download task initiated for ${res.fileName}!`, true);
                            closeDlModal();
                        } else {
                            this.showStatus(rootElement, res.message || 'Could not initiate download.', false);
                        }
                    } catch (err) {
                        console.error(`[${SCRIPT_ID}] Start download error:`, err);
                        this.showStatus(rootElement, 'Network error starting download.', false);
                    } finally {
                        if (spinner) spinner.classList.add('hidden');
                        if (confirmBtn) confirmBtn.disabled = false;
                    }
                };
            }
        },

        refreshCatalog: function (gridContainer) {
            if (!gridContainer) return;
            const url = `${window.location.origin}/${CONTROLLER_PATH}/RefreshCatalog`;

            fetch(url, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.text())
                .then(html => {
                    gridContainer.innerHTML = html;
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Refresh catalog error:`, err));
        },

        showStatus: function (rootElement, message, isSuccess) {
            const box = rootElement.querySelector('#catalog-status-message');
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