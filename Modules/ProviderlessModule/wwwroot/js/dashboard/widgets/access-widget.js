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
            b.className = 'dot status-bling busy';
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

        const colors = ['ok', 'warn', 'bad'];
        const setBling = (sel, s) => {
            const el = this.shell.querySelector(sel);
            if (el) el.className = `dot status-bling ${colors[s] || colors[2]}`;
        };

        setBling('#bling-reg', data.registry);
        setBling('#bling-tun', data.tunnel);
        setBling('#bling-png', data.reachability);
    },

    dispose: function () {
        this.stop();
    }
};