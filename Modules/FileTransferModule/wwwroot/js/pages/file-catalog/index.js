/**
 * file-catalog/index.js (Swarm File Catalog Browser)
 * Local catalogue browsing with filters/pagination, direct node probing, get-info, batch download,
 * and the single-file download modal.
 */
(function () {
    const SCRIPT_ID = "ftt-catalog";
    const WRAPPER_ID = "file-catalog-wrapper";
    const CONTROLLER_PATH = "FileTransfer/FileCatalog";

    let root = {};
    let currentPage = 0;
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing File Catalog Controller.`);
            this.bindEvents(root);
        },

        getAntiForgeryToken: function () {
            const input = document.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : null;
        },

        bindEvents: function (rootElement) {
            const gridContainer = rootElement.querySelector('#catalog-grid-container');
            const formQuery = rootElement.querySelector('#form-query-node');
            const btnRefresh = rootElement.querySelector('#btn-refresh-catalog');
            const btnSync = rootElement.querySelector('#btn-sync-now');
            const btnApplyFilters = rootElement.querySelector('#btn-apply-filters');
            const btnClearFilters = rootElement.querySelector('#btn-clear-filters');
            const btnBatchDownload = rootElement.querySelector('#btn-batch-download');

            const downloadModal = document.getElementById('download-prompt-modal');
            const formDownload = document.getElementById('form-start-download');

            if (btnRefresh) {
                btnRefresh.onclick = () => { currentPage = 0; this.loadPage(rootElement, 0); };
            }

            if (btnSync) {
                btnSync.onclick = () => this.triggerSync(rootElement, btnSync);
            }

            if (btnApplyFilters) {
                btnApplyFilters.onclick = () => { currentPage = 0; this.loadPage(rootElement, 0); };
            }

            if (btnClearFilters) {
                btnClearFilters.onclick = () => {
                    rootElement.querySelector('#filter-search').value = '';
                    rootElement.querySelector('#filter-host').value = '';
                    currentPage = 0;
                    this.loadPage(rootElement, 0);
                };
            }

            if (formQuery) {
                formQuery.onsubmit = (e) => {
                    e.preventDefault();
                    const nodeIdInput = formQuery.querySelector('#input-target-node-id');
                    const nodeId = nodeIdInput?.value?.trim();
                    if (!nodeId) return;

                    const url = `/${CONTROLLER_PATH}/QueryNode?nodeId=${encodeURIComponent(nodeId)}`;
                    fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                        .then(res => res.text())
                        .then(html => {
                            if (gridContainer) gridContainer.innerHTML = html;
                            this.updateBatchBar(rootElement);
                            this.showStatus(rootElement, `Probed node ${nodeId} directly.`, true);
                        })
                        .catch(err => {
                            console.error(`[${SCRIPT_ID}] Node probe error:`, err);
                            this.showStatus(rootElement, `Failed to query node ${nodeId}.`, false);
                        });
                };
            }

            // Modal dismiss
            const closeDlModal = () => { if (downloadModal) downloadModal.classList.add('ftt-hidden'); };
            downloadModal?.querySelector('#btn-close-download-modal')?.addEventListener('click', closeDlModal);
            downloadModal?.querySelector('#btn-cancel-download')?.addEventListener('click', closeDlModal);

            // Delegated grid clicks: download prompt, get-info, pagination, batch checkboxes
            if (gridContainer) {
                gridContainer.onclick = (e) => {
                    const downloadBtn = e.target.closest('button[data-action="prompt-download"]');
                    if (downloadBtn && downloadModal) {
                        const card = downloadBtn.closest('.catalog-entry-card');
                        const host = card?.dataset.host;
                        const hash = card?.dataset.hash;
                        const filename = card?.dataset.filename;

                        downloadModal.querySelector('#modal-host-node-id').value = host || '';
                        downloadModal.querySelector('#modal-content-hash').value = hash || '';
                        downloadModal.querySelector('#modal-display-filename').textContent = filename || 'Unknown';
                        downloadModal.querySelector('#modal-input-filename').value = filename || '';
                        const customDir = downloadModal.querySelector('#modal-input-directory');
                        if (customDir) customDir.value = '';

                        downloadModal.classList.remove('ftt-hidden');
                        return;
                    }

                    const infoBtn = e.target.closest('button[data-action="download-info"]');
                    if (infoBtn) {
                        const card = infoBtn.closest('.catalog-entry-card');
                        this.downloadFileInfo(rootElement, card?.dataset.host, card?.dataset.hash, infoBtn);
                        return;
                    }

                    const pageBtn = e.target.closest('[data-target-page]');
                    if (pageBtn && !pageBtn.disabled) {
                        const target = parseInt(pageBtn.getAttribute('data-target-page'), 10);
                        if (!isNaN(target) && target >= 0) this.loadPage(rootElement, target);
                        return;
                    }
                };

                gridContainer.addEventListener('change', (e) => {
                    if (e.target.classList.contains('catalog-batch-select')) {
                        this.updateBatchBar(rootElement);
                    }
                });
            }

            if (btnBatchDownload) {
                btnBatchDownload.onclick = () => this.startBatchDownload(rootElement);
            }

            // Submit single-file download
            if (formDownload) {
                formDownload.onsubmit = async (e) => {
                    e.preventDefault();
                    const spinner = formDownload.querySelector('#download-spinner');
                    const confirmBtn = formDownload.querySelector('#btn-confirm-download');

                    if (spinner) spinner.classList.remove('ftt-hidden');
                    if (confirmBtn) confirmBtn.disabled = true;

                    try {
                        const resp = await fetch(`/${CONTROLLER_PATH}/StartDownload`, {
                            method: 'POST',
                            body: new FormData(formDownload),
                            headers: { 'X-Requested-With': 'XMLHttpRequest' }
                        });

                        const res = await resp.json();
                        if (res.success) {
                            this.showStatus(rootElement, `Download task started for ${res.fileName}.`, true);
                            closeDlModal();
                        } else {
                            this.showStatus(rootElement, res.message || 'Could not start download.', false);
                        }
                    } catch (err) {
                        console.error(`[${SCRIPT_ID}] Start download error:`, err);
                        this.showStatus(rootElement, 'Network error starting download.', false);
                    } finally {
                        if (spinner) spinner.classList.add('ftt-hidden');
                        if (confirmBtn) confirmBtn.disabled = false;
                    }
                };
            }
        },

        loadPage: function (rootElement, page) {
            currentPage = page;
            const gridContainer = rootElement.querySelector('#catalog-grid-container');
            if (!gridContainer) return;

            const search = rootElement.querySelector('#filter-search')?.value?.trim() || '';
            const host = rootElement.querySelector('#filter-host')?.value?.trim() || '';

            const params = new URLSearchParams({ page: String(page) });
            if (search) params.set('search', search);
            if (host) params.set('hostNodeId', host);

            fetch(`/${CONTROLLER_PATH}/RefreshCatalog?${params.toString()}`, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.text())
                .then(html => {
                    gridContainer.innerHTML = html;
                    this.updateBatchBar(rootElement);
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Failed loading catalogue page:`, err));
        },

        triggerSync: function (rootElement, btn) {
            const token = this.getAntiForgeryToken();
            const body = new FormData();
            if (token) body.append('__RequestVerificationToken', token);

            if (btn) btn.disabled = true;

            fetch(`/${CONTROLLER_PATH}/TriggerCatalogConsensusSync`, {
                method: 'POST',
                body: body,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    const msg = data.success
                        ? `Sync complete: pulled ${data.recordsPulled ?? 0}, rejected ${data.recordsRejected ?? 0}.`
                        : (data.failureReason || 'Sync did not run.');
                    this.showStatus(rootElement, msg, !!data.success);
                    this.loadPage(rootElement, currentPage);
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Sync failed:`, err);
                    this.showStatus(rootElement, 'Sync failed - see console.', false);
                })
                .finally(() => { if (btn) btn.disabled = false; });
        },

        downloadFileInfo: function (rootElement, hostNodeId, contentHash, btn) {
            if (!hostNodeId || !contentHash) return;
            const token = this.getAntiForgeryToken();

            const body = new FormData();
            body.append('hostNodeId', hostNodeId);
            body.append('contentHash', contentHash);
            if (token) body.append('__RequestVerificationToken', token);

            if (btn) btn.disabled = true;

            fetch(`/${CONTROLLER_PATH}/DownloadFileInfo`, {
                method: 'POST',
                body: body,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    if (data.success) {
                        this.showStatus(rootElement, data.saved ? 'Info fetched and saved.' : 'Info fetched but not saved (per-host cap reached).', data.saved);
                        this.loadPage(rootElement, currentPage); // redraw the card with the new info
                    } else {
                        this.showStatus(rootElement, data.message || 'Could not fetch info.', false);
                    }
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Get info failed:`, err);
                    this.showStatus(rootElement, 'Get info failed - see console.', false);
                })
                .finally(() => { if (btn) btn.disabled = false; });
        },

        updateBatchBar: function (rootElement) {
            const bar = rootElement.querySelector('#batch-download-bar');
            const countEl = rootElement.querySelector('#batch-selected-count');
            const checked = rootElement.querySelectorAll('.catalog-batch-select:checked');

            if (!bar || !countEl) return;

            if (checked.length > 0) {
                countEl.textContent = String(checked.length);
                bar.classList.remove('ftt-hidden');
            } else {
                bar.classList.add('ftt-hidden');
            }
        },

        startBatchDownload: function (rootElement) {
            const checked = rootElement.querySelectorAll('.catalog-batch-select:checked');
            if (checked.length === 0) return;

            const items = Array.from(checked).map(cb => {
                const card = cb.closest('.catalog-entry-card');
                return { hostNodeId: card.dataset.host, contentHash: card.dataset.hash, customFileName: null };
            });

            const token = this.getAntiForgeryToken();
            const body = new FormData();
            body.append('requestJson', JSON.stringify({ items: items, customDirectory: null }));
            if (token) body.append('__RequestVerificationToken', token);

            const btn = rootElement.querySelector('#btn-batch-download');
            if (btn) btn.disabled = true;

            fetch(`/${CONTROLLER_PATH}/StartBatchDownload`, {
                method: 'POST',
                body: body,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    const results = data.results || [];
                    const successCount = results.filter(r => r.success).length;
                    this.showStatus(rootElement, `Started ${successCount} of ${results.length} download(s).`, successCount > 0);
                    rootElement.querySelectorAll('.catalog-batch-select:checked').forEach(cb => cb.checked = false);
                    this.updateBatchBar(rootElement);
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Batch download failed:`, err);
                    this.showStatus(rootElement, 'Batch download failed - see console.', false);
                })
                .finally(() => { if (btn) btn.disabled = false; });
        },

        showStatus: function (rootElement, message, isSuccess) {
            const box = rootElement.querySelector('#catalog-status-message');
            if (!box) return;

            box.textContent = message;
            box.className = isSuccess
                ? 'ftt-mb-4 ftt-p-3 ftt-rounded-lg ftt-text-xs ftt-font-medium ftt-bg-emerald-50 ftt-text-emerald-800 ftt-border ftt-border-emerald-200 ftt-block'
                : 'ftt-mb-4 ftt-p-3 ftt-rounded-lg ftt-text-xs ftt-font-medium ftt-bg-red-50 ftt-text-red-800 ftt-border ftt-border-red-200 ftt-block';

            setTimeout(() => box.classList.add('ftt-hidden'), 6000);
        },

        dispose: function () { }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) window.ModuleRegistry[SCRIPT_ID].init(hook);
})();
