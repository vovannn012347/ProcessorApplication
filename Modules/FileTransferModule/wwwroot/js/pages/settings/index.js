/**
 * index.js (FileTransfer Module Settings Engine)
 * General settings form, catalogue limits, the custom-field access list editor, and the
 * module status / announce panel.
 */
(function () {
    const SCRIPT_ID = "ftt-settings";
    const WRAPPER_ID = "file-sharing-settings-wrapper";
    const CATALOG_CONTROLLER_BASE = "/FileTr/FileCatalog";
    const SETTINGS_CONTROLLER_BASE = "/FileTr/Settings";

    let root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Registering settings lifecycle hooks.`);
            this.attachGeneralSettingsForm(root);
            this.attachCatalogSettingsForm(root);
            this.initAccessListEditor(root);
            this.initStatusPanel(root);
        },

        /** Any __RequestVerificationToken already rendered on the page works for any action here. */
        getAntiForgeryToken: function (container) {
            const input = container.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : null;
        },

        // ---------------------------------------------------------------------------------
        // 1. General settings - full-section AJAX swap on save, same shape as P2P's SaveP2P
        // ---------------------------------------------------------------------------------

        attachGeneralSettingsForm: function (container) {
            const form = container.querySelector('#generalSettingsForm');
            if (!form) return;

            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleSaveGeneralSettings(container, form);
            };
        },

        handleSaveGeneralSettings: function (container, form) {
            const btn = form.querySelector('#saveGeneralSettingsButton');
            const originalHtml = btn ? btn.innerHTML : "";
            if (btn) {
                btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin"></i> Saving...';
                btn.disabled = true;
            }

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(response => {
                    if (!response.ok) throw new Error(`Server returned HTTP ${response.status}`);
                    return response.text();
                })
                .then(html => {
                    const section = container.querySelector('#general-settings-section');
                    if (section) {
                        section.innerHTML = html;
                        this.attachGeneralSettingsForm(container); // old form node is gone, re-bind the new one
                    }
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] General settings save failed:`, err);
                    if (btn) {
                        btn.innerHTML = originalHtml;
                        btn.disabled = false;
                    }
                    alert('Failed to save settings. Check the console for details.');
                });
        },

        // ---------------------------------------------------------------------------------
        // 2. Catalogue limits - small form, loaded and saved via JSON
        // ---------------------------------------------------------------------------------

        attachCatalogSettingsForm: function (container) {
            const form = container.querySelector('#catalogSettingsForm');
            if (!form) return;

            fetch(`${SETTINGS_CONTROLLER_BASE}/GetCatalogSettings`, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    form.querySelector('#maxCatalogRecordsPerHost').value = data.maxCatalogRecordsPerHost;
                    form.querySelector('#directQueryDefaultPageSize').value = data.directQueryDefaultPageSize;
                    form.querySelector('#catalogRecordStaleAfterHours').value = data.catalogRecordStaleAfterHours;
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Failed loading catalogue limits:`, err));

            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleSaveCatalogSettings(container, form);
            };
        },

        handleSaveCatalogSettings: function (container, form) {
            const btn = form.querySelector('#saveCatalogSettingsButton');
            if (btn) btn.disabled = true;

            fetch(`${SETTINGS_CONTROLLER_BASE}/SaveCatalogSettings`, {
                method: 'POST',
                body: new FormData(form),
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    this.showInlineMessage(container.querySelector('#catalog-settings-message'), data.success, data.success ? 'Catalogue limits saved.' : (data.message || 'Save failed.'));
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Failed saving catalogue limits:`, err);
                    this.showInlineMessage(container.querySelector('#catalog-settings-message'), false, 'Save failed - see console.');
                })
                .finally(() => {
                    if (btn) btn.disabled = false;
                });
        },

        showInlineMessage: function (el, ok, text) {
            if (!el) return;
            el.textContent = text;
            el.classList.remove('ftt-hidden', 'ftt-bg-emerald-50', 'ftt-border-emerald-200', 'ftt-text-emerald-700', 'ftt-bg-red-50', 'ftt-border-red-200', 'ftt-text-red-700');
            el.classList.add('ftt-border', ok ? 'ftt-bg-emerald-50' : 'ftt-bg-red-50', ok ? 'ftt-border-emerald-200' : 'ftt-border-red-200', ok ? 'ftt-text-emerald-700' : 'ftt-text-red-700');
            setTimeout(() => el.classList.add('ftt-hidden'), 6000);
        },

        // ---------------------------------------------------------------------------------
        // 3. Custom field access list - dynamic rule rows, loaded/saved as one JSON blob
        // ---------------------------------------------------------------------------------

        initAccessListEditor: function (container) {
            const body = container.querySelector('#acl-rules-body');
            const template = container.querySelector('#tmpl-acl-rule-row');
            const addBtn = container.querySelector('#btn-add-acl-rule');
            const saveBtn = container.querySelector('#btn-save-acl-rules');
            if (!body || !template || !addBtn || !saveBtn) return;

            const addRow = (rule) => {
                const clone = template.content.cloneNode(true);
                const row = clone.querySelector('.acl-rule-row');
                row.querySelector('.acl-field-pattern').value = rule ? (rule.fieldNamePattern || '') : '';
                row.querySelector('.acl-value-pattern').value = rule ? (rule.valuePattern || '') : '';
                row.querySelector('.acl-action').value = rule && rule.action ? rule.action : 'Allow';
                row.querySelector('.acl-remove-row').onclick = () => row.remove();
                body.appendChild(clone);
            };

            addBtn.onclick = () => addRow(null);

            fetch(`${SETTINGS_CONTROLLER_BASE}/GetCustomFieldAccessRules`, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    body.innerHTML = '';
                    (data.rules || []).forEach(addRow);
                    const defaultSelect = container.querySelector('#acl-default-action');
                    if (defaultSelect && data.defaultAction) defaultSelect.value = data.defaultAction;
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Failed loading access-list rules:`, err));

            saveBtn.onclick = () => this.handleSaveAccessList(container);
        },

        handleSaveAccessList: function (container) {
            const rows = container.querySelectorAll('.acl-rule-row');
            const rules = Array.from(rows).map(row => ({
                fieldNamePattern: row.querySelector('.acl-field-pattern').value.trim(),
                valuePattern: row.querySelector('.acl-value-pattern').value.trim() || null,
                action: row.querySelector('.acl-action').value
            })).filter(r => r.fieldNamePattern.length > 0);

            const defaultAction = container.querySelector('#acl-default-action').value;
            const token = this.getAntiForgeryToken(container);

            const body = new FormData();
            body.append('rulesJson', JSON.stringify(rules));
            body.append('defaultAction', defaultAction);
            if (token) body.append('__RequestVerificationToken', token);

            const saveBtn = container.querySelector('#btn-save-acl-rules');
            if (saveBtn) saveBtn.disabled = true;

            fetch(`${SETTINGS_CONTROLLER_BASE}/SaveCustomFieldAccessRules`, {
                method: 'POST',
                body: body,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    this.showInlineMessage(container.querySelector('#acl-rules-message'), data.success, data.success ? 'Access list saved.' : (data.message || 'Save failed.'));
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Failed saving access-list rules:`, err);
                    this.showInlineMessage(container.querySelector('#acl-rules-message'), false, 'Save failed - see console.');
                })
                .finally(() => {
                    if (saveBtn) saveBtn.disabled = false;
                });
        },

        // ---------------------------------------------------------------------------------
        // 4. Module status & manual triggers
        // ---------------------------------------------------------------------------------

        initStatusPanel: function (container) {
            const refreshBtn = container.querySelector('#btn-refresh-status');
            const announceBtn = container.querySelector('#btn-trigger-announce');
            const walkBtn = container.querySelector('#btn-ensure-walk');

            if (refreshBtn) refreshBtn.onclick = () => this.loadStatus(container);
            if (announceBtn) announceBtn.onclick = () => this.postCatalogAction(container, 'TriggerAnnounceAll', (data) => `Announced ${data.announced} file(s) to known trackers.`);
            if (walkBtn) walkBtn.onclick = () => this.postCatalogAction(container, 'EnsureCatalogWalkRunning', (data) => data.running ? 'Catalogue sync is running.' : 'Catalogue sync did not start (is "file_node" advertised?).');

            this.loadStatus(container);
        },

        loadStatus: function (container) {
            fetch(`${CATALOG_CONTROLLER_BASE}/GetFileModuleStatus`, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    container.querySelector('#status-node-id').textContent = data.nodeId || '-';

                    const roles = [];
                    if (data.isFileTrackerRole) roles.push('file_tracker');
                    if (data.isFileNodeRole) roles.push('file_node');
                    container.querySelector('#status-roles').textContent = roles.length ? roles.join(', ') : 'none';

                    container.querySelector('#status-walk').textContent = data.isCatalogWalkRunning ? 'Running' : 'Idle';

                    const trackerIds = data.knownTrackerPeerIds || [];
                    container.querySelector('#status-trackers').textContent = trackerIds.length ? `${trackerIds.length} known` : 'None known yet';
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Failed loading module status:`, err));
        },

        postCatalogAction: function (container, action, messageFn) {
            const token = this.getAntiForgeryToken(container);
            const body = new FormData();
            if (token) body.append('__RequestVerificationToken', token);

            fetch(`${CATALOG_CONTROLLER_BASE}/${action}`, {
                method: 'POST',
                body: body,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(r => r.json())
                .then(data => {
                    const msgEl = container.querySelector('#status-action-message');
                    if (msgEl) {
                        msgEl.textContent = messageFn(data);
                        msgEl.classList.remove('ftt-hidden');
                    }
                    this.loadStatus(container);
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Action ${action} failed:`, err));
        },

        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Purging settings lifecycle.`);
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    //# sourceURL=js/modules/filetransfer/settings-index.js
})();
