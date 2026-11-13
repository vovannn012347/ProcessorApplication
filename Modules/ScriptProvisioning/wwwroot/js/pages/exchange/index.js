/**
 * exchange/index.js (Script Exchange page)
 * Network packages (install / replace), install task progress, and publishing installed builds.
 * Everything that came from another node (file names, ids, messages) is inserted as text, never as HTML.
 */
(function () {
    const SCRIPT_ID = "spv-exchange";
    const WRAPPER_ID = "script-exchange-wrapper";
    const CONTROLLER_PATH = "ScriptProvisioning/Exchange";
    const TASK_POLL_MS = 1500;

    // ui.css classes: badge tones and buttons
    const BADGE = "badge ";
    const TONE = { green: "ok", amber: "warn", sky: "", indigo: "", red: "bad", gray: "" };
    const BUTTON = "btn sm ";
    const BUTTON_TONE = { primary: "", warn: "", neutral: "" };
    const CELL = "";

    let root = null;
    let pollTimer = null;

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            const q = (s) => root.querySelector(s);
            q('#spv-btn-refresh-remote').onclick = () => this.loadRemote();
            q('#spv-btn-refresh-builds').onclick = () => this.loadBuilds();
            q('#spv-btn-probe').onclick = () => this.probe();
            q('#spv-probe-node').onkeydown = (e) => { if (e.key === 'Enter') this.probe(); };

            this.loadRemote();
            this.loadBuilds();
            this.loadTasks();
        },

        // ------------------------------------------------------------ helpers

        el: function (tag, cls, text) {
            const e = document.createElement(tag);
            if (cls) e.className = cls;
            if (text !== undefined && text !== null) e.textContent = String(text);
            return e;
        },

        badge: function (text, tone, title) {
            const b = this.el('span', BADGE + (TONE[tone] || TONE.gray), text);
            if (title) b.title = title;
            return b;
        },

        button: function (text, tone, onClick, title) {
            const b = this.el('button', BUTTON + (BUTTON_TONE[tone] || BUTTON_TONE.primary), text);
            b.type = 'button';
            if (title) b.title = title;
            b.onclick = () => onClick(b);
            return b;
        },

        short: (h) => (h || '').substring(0, 12),

        size: function (bytes) {
            if (!bytes && bytes !== 0) return '';
            if (bytes < 1024) return bytes + ' B';
            if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
            return (bytes / 1048576).toFixed(1) + ' MB';
        },

        token: function () {
            const input = root.querySelector('input[name="__RequestVerificationToken"]') || document.querySelector('input[name="__RequestVerificationToken"]');
            return input ? input.value : '';
        },

        get: function (action) {
            return fetch(`/${CONTROLLER_PATH}/${action}`, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); });
        },

        post: function (action, payload) {
            const body = new FormData();
            for (const k in payload) body.append(k, payload[k]);
            body.append('__RequestVerificationToken', this.token());
            return fetch(`/${CONTROLLER_PATH}/${action}`, { method: 'POST', body, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(r => { if (!r.ok) throw new Error(`HTTP ${r.status}`); return r.json(); });
        },

        status: function (message, ok) {
            const box = root.querySelector('#spv-status');
            box.textContent = message;
            box.className = 'msg ' + (ok ? 'ok' : 'warn');
            clearTimeout(this._statusTimer);
            this._statusTimer = setTimeout(() => box.classList.add('is-hidden'), 8000);
        },

        // ------------------------------------------------------------ network

        loadRemote: function () {
            return this.get('Remote')
                .then(list => this.renderRemote(list))
                .catch(err => this.status('Could not load the network list: ' + err.message, false));
        },

        renderRemote: function (list) {
            const body = root.querySelector('#spv-remote-body');
            body.replaceChildren();
            root.querySelector('#spv-remote-empty').classList.toggle('is-hidden', list.length > 0);

            for (const p of list) {
                const tr = this.el('tr', '');

                const script = this.el('td', '');
                script.appendChild(this.el('div', 'wrap', p.scriptId));
                script.appendChild(this.el('div', 'mono aux wrap', p.fileName));
                tr.appendChild(script);

                tr.appendChild(this.el('td', 'nowrap', p.version));
                tr.appendChild(this.el('td', 'mono aux', p.hashPrefix));
                tr.appendChild(this.el('td', 'nowrap', this.size(p.sizeBytes)));

                const hosts = this.el('td', 'mono aux wrap');
                hosts.textContent = p.hostNodeIds.length ? p.hostNodeIds.join(', ') : (p.publishedHere ? '(this node)' : '-');
                tr.appendChild(hosts);

                const here = this.el('td', CELL);
                const statusBadge = {
                    New: ['New script', 'sky'],
                    NewVersion: ['New version', 'indigo'],
                    Installed: ['Installed', 'green'],
                    Conflict: ['Different build installed', 'amber']
                }[p.status] || [p.status, 'gray'];
                here.appendChild(this.badge(statusBadge[0], statusBadge[1],
                    p.status === 'Conflict' ? `Installed build hash ${this.short(p.localArtifactHash)}, package ${p.hashPrefix}` : null));
                if (p.publishedHere) {
                    here.appendChild(document.createTextNode(' '));
                    here.appendChild(this.badge('Published here', 'gray'));
                }
                tr.appendChild(here);

                const actions = this.el('td', 'nowrap');
                if (p.activeTaskId) {
                    actions.appendChild(this.badge('Installing...', 'indigo'));
                } else if (p.status === 'New' || p.status === 'NewVersion') {
                    actions.appendChild(this.button('Install', 'primary', (b) => this.install(p, false, b)));
                } else if (p.status === 'Conflict') {
                    actions.appendChild(this.button('Replace', 'warn', (b) => this.install(p, true, b),
                        'Replace the installed build of this version with the package'));
                }
                tr.appendChild(actions);

                body.appendChild(tr);
            }
        },

        probe: function () {
            const input = root.querySelector('#spv-probe-node');
            const nodeId = input.value.trim();
            if (!nodeId) { this.status('Enter a node ID to ask.', false); return; }
            const btn = root.querySelector('#spv-btn-probe');
            btn.disabled = true;
            this.post('Probe', { nodeId })
                .then(r => { this.status(r.message, r.success); if (r.success) this.loadRemote(); })
                .catch(err => this.status('Probe failed: ' + err.message, false))
                .finally(() => btn.disabled = false);
        },

        install: function (p, replace, btn) {
            if (replace && !confirm(`Replace the installed ${p.scriptId} ${p.version} with the package from the network?\n\nInstalled build: ${this.short(p.localArtifactHash)}\nPackage: ${p.hashPrefix}`)) return;
            btn.disabled = true;
            this.post('Install', { contentHash: p.contentHash, replace: replace })
                .then(r => {
                    if (!r.success) this.status(r.message, false);
                    this.loadTasks();
                    this.loadRemote();
                })
                .catch(err => { btn.disabled = false; this.status('Install failed to start: ' + err.message, false); });
        },

        // ------------------------------------------------------------ tasks

        loadTasks: function () {
            clearTimeout(pollTimer);
            return this.get('Tasks')
                .then(tasks => {
                    const wasActive = this._activeTasks || 0;
                    const active = tasks.filter(t => !t.isFinished).length;
                    this._activeTasks = active;
                    this.renderTasks(tasks);
                    if (active > 0) pollTimer = setTimeout(() => this.loadTasks(), TASK_POLL_MS);
                    // A task just finished: the library and the network status changed
                    if (wasActive > active) { this.loadRemote(); this.loadBuilds(); }
                })
                .catch(() => { pollTimer = setTimeout(() => this.loadTasks(), TASK_POLL_MS * 4); });
        },

        renderTasks: function (tasks) {
            const section = root.querySelector('#spv-tasks-section');
            const list = root.querySelector('#spv-tasks-list');
            section.classList.toggle('is-hidden', tasks.length === 0);
            list.replaceChildren();

            const tone = {
                Installed: 'green', AlreadyInstalled: 'green', Conflict: 'amber', Failed: 'red'
            };
            const label = {
                Queued: 'Queued', FetchingManifest: 'Contacting host', Downloading: 'Downloading', Verifying: 'Checking',
                Installing: 'Installing', Installed: 'Installed', AlreadyInstalled: 'Already installed', Conflict: 'Not replaced', Failed: 'Failed'
            };

            for (const t of tasks) {
                const li = this.el('li', '');
                const head = this.el('div', 'actions');
                const name = this.el('div', 'wrap',
                    (t.scriptId || t.fileName) + (t.version ? ' ' + t.version : ''));
                head.appendChild(name);
                head.appendChild(this.badge(label[t.state] || t.state, tone[t.state] || 'indigo'));
                li.appendChild(head);

                if (!t.isFinished && (t.state === 'Downloading' || t.progress > 0)) {
                    const bar = this.el('div', 'bar');
                    const fill = this.el('i', '');
                    fill.style.width = Math.min(100, Math.max(0, t.progress || 0)) + '%';
                    bar.appendChild(fill);
                    li.appendChild(bar);
                }

                li.appendChild(this.el('div', 'aux wrap', t.message));

                if (t.report && t.report.length) {
                    const ul = this.el('ul', 'aux');
                    for (const line of t.report) ul.appendChild(this.el('li', 'wrap', line));
                    li.appendChild(ul);
                }
                list.appendChild(li);
            }
        },

        // ------------------------------------------------------------ publish

        loadBuilds: function () {
            return this.get('Builds')
                .then(list => this.renderBuilds(list))
                .catch(err => this.status('Could not load installed builds: ' + err.message, false));
        },

        renderBuilds: function (list) {
            const body = root.querySelector('#spv-builds-body');
            body.replaceChildren();
            root.querySelector('#spv-builds-empty').classList.toggle('is-hidden', list.length > 0);

            for (const b of list) {
                const tr = this.el('tr', '');

                const script = this.el('td', '');
                script.appendChild(this.el('div', 'wrap', b.label || b.scriptId));
                if (b.label) script.appendChild(this.el('div', 'mono aux wrap', b.scriptId));
                tr.appendChild(script);

                tr.appendChild(this.el('td', 'nowrap', b.version));
                tr.appendChild(this.el('td', 'mono aux', this.short(b.artifactHash)));

                const origin = this.el('td', 'aux wrap');
                origin.textContent = b.origin
                    ? `from ${b.origin.hostNodeIds.join(', ') || 'network'}, ${new Date(b.origin.installedUtc).toLocaleString()}${b.origin.replaced ? ' (replaced local build)' : ''}`
                    : 'local';
                tr.appendChild(origin);

                const shared = this.el('td', CELL);
                if (b.published) {
                    shared.appendChild(b.seeding
                        ? this.badge(b.published.hidden ? 'Shared (unlisted)' : 'Shared', 'green', b.published.fileName)
                        : this.badge('Not seeding', 'amber', 'FileTransfer no longer tracks the package: publish again'));
                } else if (b.blocker) {
                    shared.appendChild(this.badge("Can't share", 'red', b.blocker));
                } else {
                    shared.appendChild(this.badge('Not shared', 'gray'));
                }
                tr.appendChild(shared);

                const actions = this.el('td', 'nowrap');
                if (b.published) {
                    if (!b.seeding) actions.appendChild(this.button('Publish again', 'primary', (btn) => this.publish(b, btn)));
                    actions.appendChild(document.createTextNode(' '));
                    actions.appendChild(this.button('Stop sharing', 'neutral', (btn) => this.unpublish(b, btn)));
                } else {
                    const btn = this.button('Publish', 'primary', (x) => this.publish(b, x), b.blocker || null);
                    btn.disabled = !!b.blocker;
                    actions.appendChild(btn);
                }
                tr.appendChild(actions);

                body.appendChild(tr);
            }
        },

        publish: function (b, btn) {
            btn.disabled = true;
            const prev = btn.textContent;
            btn.textContent = 'Packing...';
            const hidden = root.querySelector('#spv-publish-hidden').checked;
            this.post('Publish', { buildId: b.scriptIndexId, hidden: hidden })
                .then(r => { this.status(r.message, r.success); this.loadBuilds(); this.loadRemote(); })
                .catch(err => { this.status('Publish failed: ' + err.message, false); btn.disabled = false; btn.textContent = prev; });
        },

        unpublish: function (b, btn) {
            if (!confirm(`Stop sharing ${b.label || b.scriptId} ${b.version}? The installed build stays; nodes that already have the package can keep sharing it.`)) return;
            btn.disabled = true;
            this.post('Unpublish', { buildId: b.scriptIndexId })
                .then(r => { this.status(r.message, r.success); this.loadBuilds(); this.loadRemote(); })
                .catch(err => { this.status('Failed: ' + err.message, false); btn.disabled = false; });
        },

        dispose: function () {
            clearTimeout(pollTimer);
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) window.ModuleRegistry[SCRIPT_ID].init(hook);
})();
