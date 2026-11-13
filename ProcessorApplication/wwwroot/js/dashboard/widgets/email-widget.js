window.WidgetLibrary = window.WidgetLibrary || {};

window.WidgetLibrary["main-email-status"] = {
    shell: null,
    pollTimer: null,

    init: function (shell, config, hub) {
        this.shell = shell;
        this.startPolling();
    },

    startPolling: function () {
        if (this.pollTimer) clearInterval(this.pollTimer);
        this.refresh();
        this.pollTimer = setInterval(() => this.refresh(), 10000);
    },

    stop: function () {
        if (this.pollTimer) clearInterval(this.pollTimer);
        this.pollTimer = null;
        console.log("[Email Widget] Polling stopped for standby.");
    },

    refresh: async function () {
        const bling = this.shell.querySelector('.status-bling');
        if (bling) {
            bling.className = 'dot status-bling busy';
        }

        try {
            const res = await fetch(`/Main/Dashboard/GetUpdate?widgetId=main-email-status`);
            if (res.ok) {
                this.updateUI(await res.json());
            } else {
                this.updateUI({ state: 2 });
            }
        } catch (err) {
            this.updateUI({ state: 2 });
        }
    },

    updateUI: function (data) {
        if (!data) return;
        const bling = this.shell.querySelector('.status-bling');
        const addrEl = this.shell.querySelector('#email-address-display');
        const statusEl = this.shell.querySelector('#email-status-text');
        const syncEl = this.shell.querySelector('#email-last-sync');

        if (addrEl) addrEl.innerText = data.address || 'N/A';
        if (statusEl) statusEl.innerText = data.statusText || 'Error';
        if (syncEl) syncEl.innerText = data.lastChecked || '--:--:--';

        if (bling) {
            const colors = ['ok', 'warn', 'bad'];
            bling.className = `dot status-bling ${colors[data.state] || colors[2]}`;
        }
    },

    dispose: function () {
        this.stop();
    }
};