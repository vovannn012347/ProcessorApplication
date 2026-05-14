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
            b.className = 'status-bling mm-w-1.5 mm-h-1.5 mm-rounded-full mm-bg-slate-400 mm-scale-125 mm-transition-all mm-duration-75';
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

        const colors = ['mm-bg-green-500', 'mm-bg-amber-500', 'mm-bg-red-500'];
        const setBling = (sel, s) => {
            const el = this.shell.querySelector(sel);
            if (el) el.className = `status-bling mm-w-1.5 mm-h-1.5 mm-rounded-full mm-transition-colors mm-duration-500 ${colors[s] || colors[2]}`;
        };

        setBling('#bling-eng', data.eng);
        setBling('#bling-idx', data.idx);
        setBling('#bling-ops', data.ops);
    },

    dispose: function () {
        this.stop();
    }
};