(function () {
    const SCRIPT_ID = "main-settings-ingest";
    const WRAPPER_ID = "settings-ingest-wrapper";
    const BASE = "/Main/SettingsIngest/";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        init: function (el) {
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;
            this.root = root;

            // re-ingest forms: swap the page with the response
            root.querySelectorAll('form.ingest-form').forEach(form => {
                form.onsubmit = (e) => {
                    e.preventDefault();
                    const question = form.dataset.confirm;
                    if (question && !window.confirm(question)) return;
                    const btn = form.querySelector('button[type="submit"]');
                    if (btn) { btn.disabled = true; btn.innerHTML = '<i class="fa-solid fa-sync fa-spin"></i> Applying...'; }
                    this.post(form.action, new FormData(form), 'text').then(html => this.swap(html)).catch(err => this.message(err.message, true));
                };
            });

            // filter
            const filter = root.querySelector('#st-filter');
            if (filter) filter.oninput = () => this.applyFilter(filter.value);

            // edit: Enter saves, Esc reverts
            root.querySelectorAll('tr.st-row').forEach(row => {
                const input = row.querySelector('.st-val');
                const save = row.querySelector('.st-save');
                const del = row.querySelector('.st-del');
                if (input) {
                    input.oninput = () => save.classList.toggle('is-hidden', input.value === input.dataset.orig);
                    input.onkeydown = (e) => {
                        if (e.key === 'Enter') { e.preventDefault(); if (input.value !== input.dataset.orig) this.save(row); }
                        if (e.key === 'Escape') { input.value = input.dataset.orig; save.classList.add('is-hidden'); }
                    };
                }
                if (save) save.onclick = () => this.save(row);
                if (del) del.onclick = () => this.remove(row);
            });

            // add
            const addRow = root.querySelector('tr.st-add');
            const addBtn = root.querySelector('.st-addbtn');
            if (addRow && addBtn) {
                const doAdd = () => this.add(addRow);
                addBtn.onclick = doAdd;
                addRow.querySelectorAll('input').forEach(i => i.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); doAdd(); } });
            }

            if (filter && filter.value) this.applyFilter(filter.value);
        },

        form: function (fields) {
            const fd = new FormData();
            const token = this.root.querySelector('input[name="__RequestVerificationToken"]');
            if (token) fd.append('__RequestVerificationToken', token.value);
            Object.keys(fields).forEach(k => fd.append(k, fields[k]));
            return fd;
        },

        post: function (url, body, as) {
            return fetch(url, { method: 'POST', body, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(res => {
                    if (res.status === 401) { window.location.reload(); throw new Error('Signed out'); }
                    if (!res.ok) throw new Error(`HTTP ${res.status}`);
                    return as === 'text' ? res.text() : res.json();
                });
        },

        save: function (row) {
            const input = row.querySelector('.st-val');
            const btn = row.querySelector('.st-save');
            btn.disabled = true;
            this.post(BASE + 'SetValue', this.form({ area: row.dataset.area, key: row.dataset.key, value: input.value, add: false }))
                .then(r => {
                    btn.disabled = false;
                    if (!r.ok) return this.message(r.error, true);
                    if (row.dataset.sensitive === '1') { input.value = ''; input.placeholder = '•••• hidden, type to replace'; input.dataset.orig = ''; }
                    else input.dataset.orig = input.value;
                    btn.classList.add('is-hidden');
                    this.message(`${row.dataset.area}:${row.dataset.key} saved.`);
                    if (!row.querySelector('.st-del')) this.reload(row.dataset.area, row.dataset.key);
                })
                .catch(err => { btn.disabled = false; this.message(err.message, true); });
        },

        remove: function (row) {
            if (!window.confirm(`Delete the stored value of ${row.dataset.area}:${row.dataset.key}? The settings file's value (if any) applies again.`)) return;
            this.post(BASE + 'DeleteValue', this.form({ area: row.dataset.area, key: row.dataset.key }))
                .then(r => r.ok ? this.reload(null, null, `${row.dataset.area}:${row.dataset.key} deleted.`) : this.message(r.error, true))
                .catch(err => this.message(err.message, true));
        },

        add: function (addRow) {
            const area = addRow.querySelector('[name="area"]').value;
            const key = addRow.querySelector('[name="key"]').value.trim();
            const value = addRow.querySelector('[name="value"]').value;
            if (!key) return this.message('Enter the setting key (Section:Name).', true);
            this.post(BASE + 'SetValue', this.form({ area, key, value, add: true }))
                .then(r => r.ok ? this.reload(area, key, `${area}:${key} added.`) : this.message(r.error, true))
                .catch(err => this.message(err.message, true));
        },

        /** Reloads the page content, then optionally highlights one row and shows a message. */
        reload: function (area, key, text) {
            const filter = this.root.querySelector('#st-filter');
            const keep = filter ? filter.value : '';
            fetch(BASE + 'Index', { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(res => res.text())
                .then(html => {
                    this.swap(html);
                    const f = this.root.querySelector('#st-filter');
                    if (f && keep) { f.value = keep; this.applyFilter(keep); }
                    if (text) this.message(text);
                    if (area && key) {
                        const row = Array.from(this.root.querySelectorAll('tr.st-row'))
                            .find(r => r.dataset.area.toLowerCase() === area.toLowerCase() && r.dataset.key.toLowerCase() === key.toLowerCase());
                        if (row) { row.classList.add('hit'); row.scrollIntoView({ block: 'center' }); }
                    }
                })
                .catch(err => this.message(err.message, true));
        },

        swap: function (html) {
            const doc = new DOMParser().parseFromString(html, 'text/html');
            const fresh = doc.getElementById(WRAPPER_ID);
            this.root.innerHTML = fresh ? fresh.innerHTML : html;
            this.init(this.root);
        },

        applyFilter: function (text) {
            const q = text.trim().toLowerCase();
            const rows = Array.from(this.root.querySelectorAll('tr.st-row'));
            const visible = new Set();
            rows.forEach(row => {
                const input = row.querySelector('.st-val');
                const hay = (row.dataset.area + ':' + row.dataset.key + ' ' + (input && row.dataset.sensitive !== '1' ? input.value : '') + ' ' + row.textContent).toLowerCase();
                const show = !q || hay.includes(q);
                row.classList.toggle('is-hidden', !show);
                if (show) { visible.add(row.dataset.area); visible.add(row.dataset.area + '|' + row.dataset.section); }
            });
            this.root.querySelectorAll('tr.grp').forEach(r => r.classList.toggle('is-hidden', !visible.has(r.dataset.area)));
            this.root.querySelectorAll('tr.sub').forEach(r => r.classList.toggle('is-hidden', !visible.has(r.dataset.area + '|' + r.dataset.section)));
        },

        message: function (text, isError) {
            const box = this.root.querySelector('#st-msg');
            if (!box) return;
            box.innerHTML = '';
            const div = document.createElement('div');
            div.className = 'msg ' + (isError ? 'warn' : 'ok');
            div.textContent = text;
            box.appendChild(div);
        },

        dispose: function (el) { }
    };

    const existing = document.getElementById(WRAPPER_ID);
    if (existing) {
        window.ModuleRegistry[SCRIPT_ID].init(existing);
    }

    //# sourceURL=js/modules/main-settings-ingest.js
})();
