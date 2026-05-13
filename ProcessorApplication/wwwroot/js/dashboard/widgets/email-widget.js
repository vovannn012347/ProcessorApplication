window.WidgetLibrary = window.WidgetLibrary || {};

window.WidgetLibrary["main-email-status"] = {
    shell: null,
    pollTimer: null,

    init: function (shell, config, hub) {
        this.shell = shell;
        this.startPolling();
    },

    startPolling: function () {
        this.refresh();
        this.pollTimer = setInterval(() => this.refresh(), 10000);
    },

    refresh: async function () {
        const bling = this.shell.querySelector('.status-bling');

        // Blink to Grey on Request Start
        if (bling) {
            bling.className = 'status-bling mm-w-2 mm-h-2 mm-rounded-full mm-bg-slate-400 mm-scale-110 mm-transition-all mm-duration-75';
        }

        try {
            const res = await fetch(`/Main/Dashboard/GetUpdate?widgetId=main-email-status`);
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

        const addrEl = this.shell.querySelector('#email-address-display');
        const statusEl = this.shell.querySelector('#email-status-text');
        const syncEl = this.shell.querySelector('#email-last-sync');
        const bling = this.shell.querySelector('.status-bling');

        if (addrEl) addrEl.innerText = data.address || 'N/A';
        if (statusEl) statusEl.innerText = data.statusText || 'Sync Error';
        if (syncEl) syncEl.innerText = data.lastChecked || '--:--:--';

        if (bling) {
            const colors = ['mm-bg-green-500', 'mm-bg-amber-500', 'mm-bg-red-500'];
            bling.className = `status-bling mm-w-2 mm-h-2 mm-rounded-full mm-transition-colors mm-duration-500 ${colors[data.state] || colors[2]}`;
        }
    },

    dispose: function () {
        if (this.pollTimer) clearInterval(this.pollTimer);
    }
};