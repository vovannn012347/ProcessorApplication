window.WidgetLibrary = window.WidgetLibrary || {};

window.WidgetLibrary["process-status"] = {
    shell: null,
    pollTimer: null,

    init: function (shell, config, hub) {
        this.shell = shell;
        this.startPolling();
    },

    startPolling: function () {
        if (this.pollTimer) clearInterval(this.pollTimer);
        this.refresh();
        this.pollTimer = setInterval(() => this.refresh(), 12000);
    },

    stop: function () {
        if (this.pollTimer) clearInterval(this.pollTimer);
        this.pollTimer = null;
    },

    refresh: async function () {
        const blings = this.shell.querySelectorAll('.status-bling');
        blings.forEach(b => {
            b.className = 'dot status-bling busy';
        });

        try {
            const res = await fetch(`/Main/Dashboard/GetUpdate?widgetId=process-status`);
            if (res.ok) {
                this.updateUI(await res.json());
            } else {
                this.updateUI({ eng: 2, idx: 2, ops: 2 });
            }
        } catch (err) {
            this.updateUI({ eng: 2, idx: 2, ops: 2 });
        }
    },

    updateUI: function (data) {
        if (!data) return;
        const updateText = (id, val) => {
            const el = this.shell.querySelector(id);
            if (el) el.innerText = val !== undefined ? val : '0';
        };

        updateText('#stat-scripts', data.scripts);
        updateText('#stat-active', data.active);
        updateText('#stat-total', data.total);
        updateText('#stat-today', data.today);
        updateText('#process-sync-time', data.sync || '--:--:--');

        const colors = ['ok', 'warn', 'bad'];
        const setBling = (sel, s) => {
            const el = this.shell.querySelector(sel);
            if (el) el.className = `dot status-bling ${colors[s] || colors[2]}`;
        };

        setBling('#bling-eng', data.eng);
        setBling('#bling-idx', data.idx);
        setBling('#bling-ops', data.ops);
    },

    dispose: function () {
        this.stop();
    }
};