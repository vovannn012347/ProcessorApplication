/**
 * file-archive/index.js (File Archive & Local Seeding Page Handler)
 * Upload, track-existing-path, import-ptorrent-and-download, and the edit-info modal
 * (visibility / allow-download / description / custom fields).
 */
(function () {
    const SCRIPT_ID = "ftt-archive";
    const WRAPPER_ID = "file-archive-wrapper";
    const CONTROLLER_PATH = "FileTr/FileArchive";

    let root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing File Archive Controller.`);
            this.bindEvents(root);
        },

        getAntiForgeryToken: function () {
            const input = document.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : null;
        },

        bindEvents: function (rootElement) {
            const tableContainer = rootElement.querySelector('#archive-table-container');

            this.bindModalToggle(rootElement, '#btn-open-ingest-modal', '#ingest-file-modal', '#btn-close-ingest-modal', '#btn-cancel-ingest', '#form-ingest-file');
            this.bindModalToggle(rootElement, '#btn-open-track-modal', '#track-path-modal', '#btn-close-track-modal', '#btn-cancel-track', '#form-track-path');
            this.bindModalToggle(rootElement, '#btn-open-import-modal', '#import-ptorrent-modal', '#btn-close-import-modal', '#btn-cancel-import', '#form-import-ptorrent');
            this.bindModalToggle(rootElement, null, '#edit-info-modal', '#btn-close-edit-modal', '#btn-cancel-edit', '#form-edit-info');

            const btnRefresh = rootElement.querySelector('#btn-refresh-archive');
            if (btnRefresh) btnRefresh.onclick = () => this.refreshTrackedList(tableContainer);

            this.bindUploadForm(rootElement, tableContainer);
            this.bindTrackPathForm(rootElement, tableContainer);
            this.bindImportPtorrentForm(rootElement);
            this.bindEditInfoForm(rootElement, tableContainer);

            if (tableContainer) {
                tableContainer.onclick = (e) => {
                    const btn = e.target.closest('button[data-action]');
                    if (!btn) return;

                    const row = btn.closest('tr');
                    const hash = row?.dataset.hash;
                    const action = btn.dataset.action;

                    if (action === 'edit-info') {
                        this.openEditModal(rootElement, row);
                    } else if (action === 'delete-file') {
                        this.executePostAction('DeleteFile', { contentHash: hash, deletePhysical: false },
                            'Stop seeding and remove this file from tracking?',
                            () => this.refreshTrackedList(tableContainer));
                    }
                };
            }
        },

        bindModalToggle: function (rootElement, openSelector, modalSelector, closeSelector, cancelSelector, formSelector) {
            const modal = document.getElementById(modalSelector.replace('#', ''));
            if (!modal) return;

            const form = document.getElementById(formSelector.replace('#', ''));
            const close = () => { modal.classList.add('ftt-hidden'); form?.reset(); };

            if (openSelector) {
                const openBtn = rootElement.querySelector(openSelector);
                if (openBtn) openBtn.onclick = () => modal.classList.remove('ftt-hidden');
            }

            const closeBtn = modal.querySelector(closeSelector);
            const cancelBtn = modal.querySelector(cancelSelector);
            if (closeBtn) closeBtn.onclick = close;
            if (cancelBtn) cancelBtn.onclick = close;

            modal._close = close; // stashed so other handlers (e.g. after successful submit) can reuse it
        },

        bindUploadForm: function (rootElement, tableContainer) {
            const form = document.getElementById('form-ingest-file');
            const modal = document.getElementById('ingest-file-modal');
            if (!form) return;

            form.onsubmit = async (e) => {
                e.preventDefault();
                const spinner = form.querySelector('#ingest-spinner');
                const submitBtn = form.querySelector('#btn-submit-ingest');
                if (spinner) spinner.classList.remove('ftt-hidden');
                if (submitBtn) submitBtn.disabled = true;

                try {
                    const resp = await fetch(`/${CONTROLLER_PATH}/IngestFile`, {
                        method: 'POST', body: new FormData(form), headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });
                    const data = await resp.json();
                    if (data.success) {
                        this.showStatus(rootElement, `Ingested and seeding "${data.fileName}".`, true);
                        modal?._close();
                        this.refreshTrackedList(tableContainer);
                    } else {
                        this.showStatus(rootElement, data.message || 'File ingestion failed.', false);
                    }
                } catch (err) {
                    console.error(`[${SCRIPT_ID}] Ingest upload error:`, err);
                    this.showStatus(rootElement, 'Network failure during ingestion.', false);
                } finally {
                    if (spinner) spinner.classList.add('ftt-hidden');
                    if (submitBtn) submitBtn.disabled = false;
                }
            };
        },

        bindTrackPathForm: function (rootElement, tableContainer) {
            const form = document.getElementById('form-track-path');
            const modal = document.getElementById('track-path-modal');
            if (!form) return;

            form.onsubmit = async (e) => {
                e.preventDefault();
                const submitBtn = form.querySelector('#btn-submit-track');
                if (submitBtn) submitBtn.disabled = true;

                try {
                    const resp = await fetch(`/${CONTROLLER_PATH}/TrackExistingFile`, {
                        method: 'POST', body: new FormData(form), headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });
                    const data = await resp.json();
                    if (data.success) {
                        this.showStatus(rootElement, `Now tracking "${data.fileName}".`, true);
                        modal?._close();
                        this.refreshTrackedList(tableContainer);
                    } else {
                        this.showStatus(rootElement, data.message || 'Could not track file.', false);
                    }
                } catch (err) {
                    console.error(`[${SCRIPT_ID}] Track path error:`, err);
                    this.showStatus(rootElement, 'Network failure tracking file.', false);
                } finally {
                    if (submitBtn) submitBtn.disabled = false;
                }
            };
        },

        bindImportPtorrentForm: function (rootElement) {
            const form = document.getElementById('form-import-ptorrent');
            const modal = document.getElementById('import-ptorrent-modal');
            if (!form) return;

            form.onsubmit = async (e) => {
                e.preventDefault();
                const submitBtn = form.querySelector('#btn-submit-import');
                if (submitBtn) submitBtn.disabled = true;

                try {
                    const resp = await fetch(`/${CONTROLLER_PATH}/IngestPtorrentAndDownload`, {
                        method: 'POST', body: new FormData(form), headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });
                    const data = await resp.json();
                    if (data.success) {
                        this.showStatus(rootElement, `Download started for "${data.fileName}". Check File Exchange for progress.`, true);
                        modal?._close();
                    } else {
                        this.showStatus(rootElement, data.message || 'Could not import .ptorrent.', false);
                    }
                } catch (err) {
                    console.error(`[${SCRIPT_ID}] Import ptorrent error:`, err);
                    this.showStatus(rootElement, 'Network failure importing .ptorrent.', false);
                } finally {
                    if (submitBtn) submitBtn.disabled = false;
                }
            };
        },

        openEditModal: function (rootElement, row) {
            const modal = document.getElementById('edit-info-modal');
            if (!modal || !row) return;

            modal.querySelector('#edit-content-hash').value = row.dataset.hash || '';
            modal.querySelector('#edit-file-name').textContent = row.dataset.filename || '';
            modal.querySelector('#edit-is-hidden').checked = row.dataset.hidden === 'true';
            modal.querySelector('#edit-allow-download').checked = row.dataset.allowDownload === 'true';
            modal.querySelector('#edit-description').value = row.dataset.description || '';

            let fieldsText = '';
            try {
                const fields = JSON.parse(row.dataset.customFields || '{}');
                fieldsText = Object.entries(fields).map(([k, v]) => `${k}: ${v}`).join('\n');
            } catch (err) {
                console.warn(`[${SCRIPT_ID}] Could not parse existing custom fields:`, err);
            }
            modal.querySelector('#edit-custom-fields').value = fieldsText;

            modal.classList.remove('ftt-hidden');
        },

        bindEditInfoForm: function (rootElement, tableContainer) {
            const form = document.getElementById('form-edit-info');
            const modal = document.getElementById('edit-info-modal');
            if (!form) return;

            form.onsubmit = async (e) => {
                e.preventDefault();
                const submitBtn = form.querySelector('#btn-submit-edit');
                if (submitBtn) submitBtn.disabled = true;

                const fieldsLines = (form.querySelector('#edit-custom-fields').value || '').split('\n');
                const customFields = {};
                fieldsLines.forEach(line => {
                    const idx = line.indexOf(':');
                    if (idx <= 0) return;
                    const key = line.slice(0, idx).trim();
                    const value = line.slice(idx + 1).trim();
                    if (key) customFields[key] = value;
                });

                const body = new FormData();
                body.append('contentHash', form.querySelector('#edit-content-hash').value);
                body.append('isHidden', form.querySelector('#edit-is-hidden').checked);
                body.append('allowDownload', form.querySelector('#edit-allow-download').checked);
                body.append('description', form.querySelector('#edit-description').value || '');
                body.append('customFieldsJson', JSON.stringify(customFields));
                const token = this.getAntiForgeryToken();
                if (token) body.append('__RequestVerificationToken', token);

                try {
                    const resp = await fetch(`/${CONTROLLER_PATH}/UpdateSettings`, {
                        method: 'POST', body: body, headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });
                    const data = await resp.json();
                    if (data.success) {
                        this.showStatus(rootElement, 'File settings updated.', true);
                        modal?._close();
                        this.refreshTrackedList(tableContainer);
                    } else {
                        this.showStatus(rootElement, data.message || 'Could not save settings.', false);
                    }
                } catch (err) {
                    console.error(`[${SCRIPT_ID}] Update settings error:`, err);
                    this.showStatus(rootElement, 'Network failure saving settings.', false);
                } finally {
                    if (submitBtn) submitBtn.disabled = false;
                }
            };
        },

        refreshTrackedList: function (tableContainer) {
            if (!tableContainer) return;
            fetch(`/${CONTROLLER_PATH}/GetTrackedList`, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(res => res.text())
                .then(html => { tableContainer.innerHTML = html; })
                .catch(err => console.error(`[${SCRIPT_ID}] Error refreshing table:`, err));
        },

        executePostAction: function (actionName, payload, confirmMsg, callback) {
            if (confirmMsg && !confirm(confirmMsg)) return;

            const token = this.getAntiForgeryToken();
            const body = new FormData();
            for (const key in payload) body.append(key, payload[key]);
            if (token) body.append('__RequestVerificationToken', token);

            fetch(`/${CONTROLLER_PATH}/${actionName}`, {
                method: 'POST', body: body, headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.json())
                .then(data => { if (callback) callback(data); })
                .catch(err => console.error(`[${SCRIPT_ID}] Post action ${actionName} failed:`, err));
        },

        showStatus: function (rootElement, message, isSuccess) {
            const box = rootElement.querySelector('#archive-status-message');
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
