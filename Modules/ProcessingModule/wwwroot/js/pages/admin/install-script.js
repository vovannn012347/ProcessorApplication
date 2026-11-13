/**
 * install-script.js (Processor Module)
 * Admin upload of script packages (.zip): upload progress, validation report, replace confirmation.
 * The upload runs in an XMLHttpRequest, so the page stays usable while it runs.
 */
(function () {
    const SCRIPT_ID = "processor-install-script";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        root: null,
        xhr: null,
        file: null,
        stageId: null,
        busy: false,

        init: function (el) {
            this.root = el || document.getElementById('install-script-wrapper');
            if (!this.root) return;

            const $ = (id) => this.root.querySelector('#' + id);
            this.el = {
                input: $('packageInput'), dropZone: $('dropZone'), selected: $('selectedFile'),
                uploadBtn: $('uploadBtn'), pickPanel: $('pickPanel'),
                progressPanel: $('progressPanel'), progressBar: $('progressBar'), progressLabel: $('progressLabel'),
                progressValue: $('progressValue'), progressHint: $('progressHint'), cancelBtn: $('cancelBtn'),
                resultPanel: $('resultPanel'), resultIcon: $('resultIcon'), resultMessage: $('resultMessage'),
                resultSubline: $('resultSubline'), confirmBox: $('confirmBox'), overwriteBtn: $('overwriteBtn'),
                discardBtn: $('discardBtn'), facts: $('resultFacts'), reportList: $('reportList'),
                indexLogBox: $('indexLogBox'), indexLog: $('indexLog'), againBtn: $('againBtn'), libraryLink: $('libraryLink')
            };
            this.maxUploadBytes = (parseInt(this.root.dataset.maxUploadMb, 10) || 2048) * 1024 * 1024;

            this.onBeforeUnload = (e) => {
                if (!this.busy) return;
                e.preventDefault();
                e.returnValue = '';
            };
            window.addEventListener('beforeunload', this.onBeforeUnload);

            this.el.input.addEventListener('change', () => this.selectFile(this.el.input.files[0]));
            this.el.uploadBtn.addEventListener('click', () => this.upload());
            this.el.cancelBtn.addEventListener('click', () => this.cancel());
            this.el.overwriteBtn.addEventListener('click', () => this.commit(true));
            this.el.discardBtn.addEventListener('click', () => this.discard());
            this.el.againBtn.addEventListener('click', () => this.reset());

            ['dragenter', 'dragover'].forEach(evt => this.el.dropZone.addEventListener(evt, (e) => {
                e.preventDefault();
                this.el.dropZone.classList.add('hit');
            }));
            ['dragleave', 'drop'].forEach(evt => this.el.dropZone.addEventListener(evt, (e) => {
                e.preventDefault();
                this.el.dropZone.classList.remove('hit');
            }));
            this.el.dropZone.addEventListener('drop', (e) => {
                if (e.dataTransfer && e.dataTransfer.files.length) this.selectFile(e.dataTransfer.files[0]);
            });

            console.log(`[${SCRIPT_ID}] Ready.`);
        },

        token: function () {
            const input = (this.root && this.root.querySelector('input[name="__RequestVerificationToken"]'))
                || document.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : '';
        },

        selectFile: function (file) {
            this.file = null;
            this.el.selected.textContent = '';
            this.setUploadEnabled(false);
            if (!file) return;

            if (!file.name.toLowerCase().endsWith('.zip')) {
                this.el.selected.textContent = 'Only .zip files are supported.';
                return;
            }
            if (file.size > this.maxUploadBytes) {
                this.el.selected.textContent = `${file.name} is ${this.formatBytes(file.size)}, over the upload limit.`;
                return;
            }

            this.file = file;
            this.el.selected.textContent = `${file.name} · ${this.formatBytes(file.size)}`;
            this.setUploadEnabled(true);
        },

        setUploadEnabled: function (enabled) {
            this.el.uploadBtn.disabled = !enabled;
        },

        upload: function () {
            if (!this.file || this.busy) return;

            const form = new FormData();
            form.append('package', this.file, this.file.name);

            const xhr = new XMLHttpRequest();
            this.xhr = xhr;
            this.busy = true;

            this.el.pickPanel.classList.add('is-hidden');
            this.el.resultPanel.classList.add('is-hidden');
            this.el.progressPanel.classList.remove('is-hidden');
            this.el.cancelBtn.classList.remove('is-hidden');
            this.setProgress(0, 'Uploading…');

            xhr.upload.addEventListener('progress', (e) => {
                if (!e.lengthComputable) return;
                const pct = Math.floor((e.loaded / e.total) * 100);
                this.setProgress(pct, 'Uploading…', `${this.formatBytes(e.loaded)} / ${this.formatBytes(e.total)}`);
            });

            xhr.upload.addEventListener('load', () => {
                // Bytes are sent; the server is now extracting, validating and indexing.
                this.setProgress(100, 'Checking package…', '');
                this.el.progressBar.classList.add('busy');
                this.el.progressHint.textContent = 'Extracting, validating and hashing. Large packages take a little while.';
                this.el.cancelBtn.classList.add('is-hidden');
            });

            xhr.addEventListener('load', () => this.handleResponse(xhr));
            xhr.addEventListener('error', () => this.showFailure('The upload failed. Check the connection and try again.'));
            xhr.addEventListener('abort', () => {
                this.busy = false;
                this.reset();
            });

            xhr.open('POST', '/Processing/Admin/UploadScriptPackage');
            xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
            xhr.setRequestHeader('RequestVerificationToken', this.token());
            xhr.send(form);
        },

        cancel: function () {
            if (this.xhr && this.busy) this.xhr.abort();
        },

        commit: async function (overwrite) {
            if (!this.stageId || this.busy) return;
            this.busy = true;
            this.el.overwriteBtn.disabled = true;
            this.el.discardBtn.disabled = true;

            this.el.resultPanel.classList.add('is-hidden');
            this.el.progressPanel.classList.remove('is-hidden');
            this.el.cancelBtn.classList.add('is-hidden');
            this.setProgress(100, 'Installing…', '');
            this.el.progressBar.classList.add('busy');

            try {
                const body = new URLSearchParams({ stageId: this.stageId, overwrite: overwrite ? 'true' : 'false' });
                const response = await fetch('/Processing/Admin/CommitScriptInstall', {
                    method: 'POST',
                    headers: {
                        'X-Requested-With': 'XMLHttpRequest',
                        'RequestVerificationToken': this.token(),
                        'Content-Type': 'application/x-www-form-urlencoded'
                    },
                    body: body
                });
                this.handleResponse({ status: response.status, responseText: await response.text() });
            } catch (err) {
                this.showFailure('Installing failed: ' + err);
            }
        },

        discard: async function () {
            const id = this.stageId;
            this.stageId = null;
            if (id) {
                try {
                    await fetch('/Processing/Admin/DiscardScriptUpload', {
                        method: 'POST',
                        headers: {
                            'X-Requested-With': 'XMLHttpRequest',
                            'RequestVerificationToken': this.token(),
                            'Content-Type': 'application/x-www-form-urlencoded'
                        },
                        body: new URLSearchParams({ stageId: id })
                    });
                } catch (err) { /* staged files expire on their own */ }
            }
            this.reset();
        },

        handleResponse: function (xhr) {
            this.busy = false;
            this.xhr = null;
            this.el.progressBar.classList.remove('busy');

            let result = null;
            try { result = JSON.parse(xhr.responseText); } catch (e) { /* not JSON */ }

            if (!result) {
                const reason = xhr.status === 413 ? 'The file is too large for the server.'
                    : xhr.status === 400 ? 'The server refused the request (the page may be stale; reload it and try again).'
                    : `Unexpected server response (HTTP ${xhr.status}).`;
                this.showFailure(reason);
                return;
            }

            this.renderResult(result);
        },

        renderResult: function (result) {
            const status = result.status;
            const report = result.report || { items: [] };
            this.stageId = status === 'NeedsConfirmation' ? result.stage_id : null;

            this.el.progressPanel.classList.add('is-hidden');
            this.el.resultPanel.classList.remove('is-hidden');

            const styles = {
                Installed: ['fa-circle-check', 'ok-t'],
                NeedsConfirmation: ['fa-triangle-exclamation', 'warn-t'],
                Ready: ['fa-circle-check', 'info'],
                Rejected: ['fa-circle-xmark', 'bad-t'],
                Failed: ['fa-circle-xmark', 'bad-t']
            }[status] || ['fa-circle-info', 'aux'];

            this.el.resultIcon.className = `fa-solid ${styles[0]} ${styles[1]}`;
            this.el.resultMessage.textContent = result.message || status;

            const idLine = [report.script_id, report.version ? 'v' + report.version : null].filter(Boolean).join(' · ');
            this.el.resultSubline.textContent = result.installed_folder ? `${idLine} → ${result.installed_folder}` : idLine;

            this.el.confirmBox.classList.toggle('is-hidden', status !== 'NeedsConfirmation');
            this.el.overwriteBtn.disabled = false;
            this.el.discardBtn.disabled = false;
            this.el.libraryLink.classList.toggle('is-hidden', status !== 'Installed');

            // Facts
            this.el.facts.innerHTML = '';
            const facts = [
                ['Label', report.label],
                ['Entry point', report.entry_point],
                ['Files', report.file_count ? `${report.file_count} · ${this.formatBytes(report.total_bytes)}` : null],
                ['Hash', report.computed_hash ? report.computed_hash.substring(0, 16) + '…' : null, report.computed_hash],
                ['Other versions', (result.other_versions || []).join(', ') || null]
            ];
            facts.filter(f => f[1]).forEach(([label, value, title]) => {
                const box = document.createElement('div');
                box.className = 'f';
                const dt = document.createElement('label');
                dt.textContent = label;
                const dd = document.createElement('span');
                dd.className = 'mono wrap';
                dd.textContent = value;
                if (title) dd.title = title;
                box.append(dt, dd);
                this.el.facts.appendChild(box);
            });
            this.el.facts.classList.toggle('is-hidden', this.el.facts.children.length === 0);

            // Report items: errors first, then warnings, then info
            const order = { Error: 0, Warning: 1, Info: 2 };
            const items = (report.items || []).slice().sort((a, b) => order[a.level] - order[b.level]);
            this.el.reportList.innerHTML = '';
            const itemStyle = {
                Error: ['fa-circle-xmark', 'bad-t', 'msg warn'],
                Warning: ['fa-triangle-exclamation', 'warn-t', 'msg warn'],
                Info: ['fa-circle-info', 'aux', 'note']
            };
            if (items.length === 0) {
                const li = document.createElement('li');
                li.className = 'aux';
                li.textContent = 'No findings.';
                this.el.reportList.appendChild(li);
            }
            items.forEach(item => {
                const s = itemStyle[item.level] || itemStyle.Info;
                const li = document.createElement('li');
                li.className = s[2];
                const icon = document.createElement('i');
                icon.className = `fa-solid ${s[0]} ${s[1]}`;
                const area = document.createElement('span');
                area.className = 'lbl';
                area.textContent = item.area;
                const msg = document.createElement('span');
                msg.className = 'wrap';
                msg.textContent = item.message;
                li.append(icon, ' ', area, ' ', msg);
                this.el.reportList.appendChild(li);
            });

            const log = result.index_log || [];
            this.el.indexLogBox.classList.toggle('is-hidden', log.length === 0);
            this.el.indexLog.textContent = log.join('\n');
        },

        showFailure: function (message) {
            this.busy = false;
            this.xhr = null;
            this.renderResult({ status: 'Failed', message: message, report: { items: [] } });
        },

        setProgress: function (pct, label, detail) {
            this.el.progressBar.style.width = pct + '%';
            this.el.progressLabel.textContent = label;
            this.el.progressValue.textContent = detail === undefined ? pct + '%' : (detail ? `${detail} · ${pct}%` : '');
        },

        reset: function () {
            this.file = null;
            this.stageId = null;
            this.el.input.value = '';
            this.el.selected.textContent = '';
            this.setUploadEnabled(false);
            this.el.progressBar.classList.remove('busy');
            this.el.progressHint.textContent = 'You can keep using the app in another tab while this runs.';
            this.el.progressPanel.classList.add('is-hidden');
            this.el.resultPanel.classList.add('is-hidden');
            this.el.pickPanel.classList.remove('is-hidden');
        },

        formatBytes: function (bytes) {
            if (!bytes) return '0 B';
            const units = ['B', 'KB', 'MB', 'GB'];
            let i = 0, v = bytes;
            while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
            return (i === 0 ? v : v.toFixed(1)) + ' ' + units[i];
        },

        /**
         * Page unload hook. A running upload is left to finish: aborting halfway would waste it,
         * and a valid package without conflicts is installed by the server anyway.
         */
        dispose: function () {
            window.removeEventListener('beforeunload', this.onBeforeUnload);
            console.log(`[${SCRIPT_ID}] Disposed${this.busy ? ' (upload continues in background)' : ''}.`);
        }
    };

    // SELF-LAUNCHING BLOCK
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    //# sourceURL=js/modules/processor/install-script.js
})();
