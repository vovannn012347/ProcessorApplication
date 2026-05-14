window.WidgetLibrary = window.WidgetLibrary || {};

window.WidgetLibrary["access-status"] = {
    shell: null,
    pollTimer: null,

    init: function (shell, config, hub) {
        this.shell = shell;
        this.startPolling();
    },

    startPolling: function () {
        if (this.pollTimer) clearInterval(this.pollTimer);
        this.refresh();
        this.pollTimer = setInterval(() => this.refresh(), 20000);
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
            const res = await fetch(`/Main/Dashboard/GetUpdate?widgetId=access-status`);
            if (res.ok) {
                this.updateUI(await res.json());
            } else {
                this.updateUI({ registry: 2, tunnel: 2, reachability: 2 });
            }
        } catch (err) {
            this.updateUI({ registry: 2, tunnel: 2, reachability: 2 });
        }
    },

    updateUI: function (data) {
        if (!data) return;
        const urlEl = this.shell.querySelector('#access-url-text');
        const syncEl = this.shell.querySelector('#access-sync-time');

        if (urlEl) urlEl.innerText = data.url || 'None';
        if (syncEl) syncEl.innerText = data.lastChecked || '--:--:--';

        const colors = ['mm-bg-green-500', 'mm-bg-amber-500', 'mm-bg-red-500'];
        const setBling = (sel, s) => {
            const el = this.shell.querySelector(sel);
            if (el) el.className = `status-bling mm-w-1.5 mm-h-1.5 mm-rounded-full mm-transition-colors mm-duration-500 ${colors[s] || colors[2]}`;
        };

        setBling('#bling-reg', data.registry);
        setBling('#bling-tun', data.tunnel);
        setBling('#bling-png', data.reachability);
    },

    dispose: function () {
        this.stop();
    }
};