window.WidgetLibrary = window.WidgetLibrary || {};

window.WidgetLibrary["main-user-stats"] = {
    shell: null,
    pollTimer: null,

    init: function (shell, config, hub) {
        this.shell = shell;
        this.startPolling();
    },

    startPolling: function () {
        this.refresh();
        this.pollTimer = setInterval(() => this.refresh(), 15000);
    },

    refresh: async function () {
        const bling = this.shell.querySelector('.status-bling');

        // Modem logic: Turn Grey while requesting
        if (bling) {
            bling.className = 'status-bling mm-w-2 mm-h-2 mm-rounded-full mm-bg-slate-400 mm-scale-110 mm-transition-all mm-duration-75';
        }

        try {
            const res = await fetch(`/Main/Dashboard/GetUpdate?widgetId=main-user-stats`);
            if (res.ok) {
                const data = await res.json();
                this.updateUI(data);
            } else {
                this.updateUI({ state: 2 });
            }
        } catch (err) {
            this.updateUI({ state: 2 });
        }
    },

    updateUI: function (data) {
        if (!data) return;

        const totalEl = this.shell.querySelector('#user-total-count');
        const activeEl = this.shell.querySelector('#user-active-count');
        const syncEl = this.shell.querySelector('#users-last-sync');
        const bling = this.shell.querySelector('.status-bling');

        if (totalEl) totalEl.innerText = data.total || 0;
        if (activeEl) activeEl.innerText = data.active || 0;
        if (syncEl) syncEl.innerText = data.lastChecked || '--:--:--';

        // Apply health state color
        if (bling) {
            const colors = ['mm-bg-green-500', 'mm-bg-amber-500', 'mm-bg-red-500'];
            bling.className = `status-bling mm-w-2 mm-h-2 mm-rounded-full mm-transition-colors mm-duration-500 ${colors[data.state] || colors[2]}`;
        }
    },

    dispose: function () {
        if (this.pollTimer) clearInterval(this.pollTimer);
    }
};