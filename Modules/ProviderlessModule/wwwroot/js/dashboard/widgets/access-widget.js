window.WidgetLibrary = window.WidgetLibrary || {};

window.WidgetLibrary["access-status"] = {
    shell: null,
    pollTimer: null,

    /**
     * @param {HTMLElement} shell - The widget container
     */
    init: function (shell, config, hub) {
        this.shell = shell;

        // Initial manual boot
        this.startPolling();

        console.log("[Access Widget] Multi-tier monitoring initialized.");
    },

    startPolling: function () {
        // Immediate first run
        this.refresh();

        // standard 20s background sync
        this.pollTimer = setInterval(() => this.refresh(), 20000);
    },

    /**
     * Performs a modem-style refresh:
     * 1. All lights to Grey (Requesting)
     * 2. Fetch update
     * 3. Lights to State Colors (Green/Yellow/Red)
     */
    refresh: async function () {
        const blings = this.shell.querySelectorAll('.status-bling');

        // 1. PHYSICAL FEEDBACK: Blink all to Grey immediately
        blings.forEach(b => {
            b.className = 'status-bling mm-w-1.5 mm-h-1.5 mm-rounded-full mm-bg-slate-400 mm-scale-125 mm-transition-all mm-duration-75';
        });

        try {
            const res = await fetch(`/Main/Dashboard/GetUpdate?widgetId=access-status`);
            if (res.ok) {
                const data = await res.json();
                this.updateUI(data);
            } else {
                // Server error state
                this.updateUI({ registry: 2, tunnel: 2, reachability: 2, url: 'Server Error' });
            }
        } catch (err) {
            // Network/Timeout state
            this.updateUI({ registry: 2, tunnel: 2, reachability: 2, url: 'Network Offline' });
            console.warn("[Access Widget] Pipe check failed", err);
        }
    },

    /**
     * @param {object} data - { registry: int, tunnel: int, reachability: int, url: string, sync: string }
     */
    updateUI: function (data) {
        if (!data) return;

        const urlEl = this.shell.querySelector('#access-url-text');
        const syncEl = this.shell.querySelector('#access-sync-time');

        if (urlEl) urlEl.innerText = data.url || 'None';
        if (syncEl) syncEl.innerText = data.lastChecked || '--:--:--';

        // 0=Green (Ok), 1=Yellow (None/Standby), 2=Red (Error)
        const colors = ['mm-bg-green-500', 'mm-bg-amber-500', 'mm-bg-red-500'];

        const applyBling = (selector, state) => {
            const el = this.shell.querySelector(selector);
            if (el) {
                // Smooth transition back from Grey to state color
                el.className = `status-bling mm-w-1.5 mm-h-1.5 mm-rounded-full mm-transition-colors mm-duration-500 ${colors[state] || colors[2]}`;
            }
        };

        applyBling('#bling-reg', data.registry);
        applyBling('#bling-tun', data.tunnel);
        applyBling('#bling-png', data.reachability);
    },

    /**
     * Clean up timers on module unload
     */
    dispose: function () {
        if (this.pollTimer) {
            clearInterval(this.pollTimer);
            this.pollTimer = null;
        }
        console.log("[Access Widget] Polling disposed.");
    }
};