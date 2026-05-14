/**
 * main-dashboard.js
 * Orchestrates real-time telemetry and widget updates for the Core Hub.
 */
(function () {
    const SCRIPT_ID = "main-dashboard";
    const WRAPPER_ID = "dashboard-hub-wrapper";
    const HEARTBEAT_INTERVAL = 20000; // 20s server-side session keep-alive

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        hub: null,
        catalog: [],
        userSettings: {},
        widgets: {}, // Instances of widget logic
        heartbeatTimer: null,

        /**
         * Entry point for the dashboard system.
         */
        init: async function (el) {
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            try {
                // 1. Fetch available widgets and user layout settings
                const catalogRes = await fetch('/Main/Dashboard/GetCatalog');
                this.catalog = await catalogRes.json();

                await this.initSession();

                // 2. Perform initial physical layout
                this.renderDashboard(root);

                // 3. Start global maintenance loop
                this.startHeartbeatLoop();

            } catch (err) {
                console.error("[Dashboard] Critical Boot Failure:", err);
            }
        },

        /**
         * Initializes SignalR and fetches user-specific widget states.
         */
        initSession: async function () {
            const ids = this.catalog.map(m => m.id);
            const query = ids.map(id => `ids=${encodeURIComponent(id)}`).join('&');
            const settingsRes = await fetch(`/Main/Dashboard/GetUserSettings?${query}`);
            this.userSettings = await settingsRes.json();

            this.hub = new signalR.HubConnectionBuilder()
                .withUrl("/dashboardHub")
                .withAutomaticReconnect()
                .build();

            // Handle incoming server-side pushes to specific widgets
            this.hub.on("ReceiveData", (id, payload) => {
                const instance = this.widgets[id];
                if (instance && instance.updateUI) {
                    instance.updateUI(payload);
                }
            });

            await this.hub.start();
        },

        /**
         * Keeps the DashboardSession alive on the server independently of polling.
         */
        startHeartbeatLoop: function () {
            if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);
            this.heartbeatTimer = setInterval(() => {
                if (this.hub && this.hub.state === signalR.HubConnectionState.Connected) {
                    this.hub.invoke("Heartbeat").catch(() => { });
                }
            }, HEARTBEAT_INTERVAL);
        },

        /**
         * INITIAL RENDER: Creates physical DOM nodes once.
         */
        renderDashboard: function (root) {
            const expanded = root.querySelector('#expanded-grid-container');
            const tray = root.querySelector('#minimized-tray-container');
            if (!expanded || !tray) return;

            // Clear containers for fresh boot
            expanded.innerHTML = '';
            tray.innerHTML = '';

            const isSmall = window.innerWidth < 1024;

            this.catalog.forEach(m => {
                const set = this.getSet(m.id, isSmall);
                if (set.isHidden) return;

                // Clone shell from template
                const clone = document.getElementById('widget-shell-template').content.cloneNode(true);
                const shell = clone.querySelector('.widget-shell');
                shell.dataset.widgetId = m.id;

                // Set Header Content
                shell.querySelector('.widget-title').innerText = m.name;
                if (m.iconClass) {
                    m.iconClass.split(' ').forEach(c => shell.querySelector('.widget-icon').classList.add(c));
                }

                // Interaction: Manual Refresh
                shell.querySelector('.refresh-btn').onclick = (e) => {
                    e.stopPropagation();
                    this.widgets[m.id]?.refresh?.();
                };

                // Interaction: Scoped Toggle (Chevron Only)
                shell.querySelector('.chevron-trigger').onclick = (e) => {
                    e.stopPropagation();
                    this.handleToggle(m.id, isSmall);
                };

                // Initial Placement
                if (set.isCollapsed) {
                    this.applyVisualState(shell, true, 1);
                    tray.appendChild(shell);
                } else {
                    this.applyVisualState(shell, false, set.width || 1);
                    expanded.appendChild(shell);
                    this.bootWidget(m, shell, set);
                }
            });
        },

        /**
         * Toggles widget visibility and moves it physically between DOM containers.
         */
        handleToggle: async function (widgetId, isSmall) {
            const root = document.getElementById(WRAPPER_ID);
            const shell = root.querySelector(`.widget-shell[data-widget-id="${widgetId}"]`);
            if (!shell) return;

            const settings = this.userSettings[widgetId] || { small: {}, large: {}, general: {} };
            const key = isSmall ? 'small' : 'large';
            if (!settings[key]) settings[key] = {};

            const isNowCollapsing = !settings[key].isCollapsed;
            settings[key].isCollapsed = isNowCollapsing;
            this.userSettings[widgetId] = settings;

            const expandedContainer = root.querySelector('#expanded-grid-container');
            const trayContainer = root.querySelector('#minimized-tray-container');

            if (isNowCollapsing) {
                // ACTION: Stop network activity and shift to Tray
                this.widgets[widgetId]?.stop?.();
                this.applyVisualState(shell, true, 1);
                trayContainer.appendChild(shell);
            } else {
                // ACTION: Shift to Grid and resume/trigger immediate update
                this.applyVisualState(shell, false, settings[key].width || 1);
                expandedContainer.appendChild(shell);

                const manifest = this.catalog.find(x => x.id === widgetId);
                await this.bootWidget(manifest, shell, settings[key]);
            }

            // Persistence: Sync with SQLite
            this.saveSettings(widgetId, settings);
        },

        /**
         * Manages CSS classes and visibility without erashing content.
         */
        applyVisualState: function (shell, isCollapsed, width) {
            const body = shell.querySelector('.widget-body');
            const icon = shell.querySelector('#chevron-icon');

            if (isCollapsed) {
                // Minimized Tray Styles
                shell.classList.add('mm-w-64', 'mm-opacity-70', 'mm-cursor-default');
                shell.classList.remove('lg:mm-col-span-1', 'lg:mm-col-span-2', 'lg:mm-col-span-3');
                body.classList.add('mm-hidden');
                if (icon) icon.classList.add('mm-rotate-180');
            } else {
                // Expanded Grid Styles
                shell.classList.remove('mm-w-64', 'mm-opacity-70');
                if (width > 1) shell.classList.add(`lg:mm-col-span-${width}`);
                body.classList.remove('mm-hidden');
                if (icon) icon.classList.remove('mm-rotate-180');
            }
        },

        /**
         * Loads the view and logic for a widget, or resumes if already loaded.
         */
        bootWidget: async function (m, shell, layout) {
            // Resume polling if already instantiated
            if (this.widgets[m.id]) {
                this.widgets[m.id].startPolling?.();
                return;
            }

            // Inform hub to start server-side tracking
            if (this.hub?.state === "Connected") {
                this.hub.invoke("ActivateWidget", m.id);
            }

            // 1. Fetch Partial View
            const res = await fetch(`/Main/Dashboard/GetWidgetView?widgetId=${m.id}`);
            shell.querySelector('.widget-body').innerHTML = await res.text();

            // 2. Fetch Logic Script
            if (m.scriptPath) {
                const vPath = `${m.scriptPath}?v=${new Date().getTime()}`;
                const s = document.createElement('script');
                s.src = vPath;
                s.onload = () => {
                    const logic = window.WidgetLibrary?.[m.id];
                    if (logic) {
                        const instance = Object.create(logic);
                        this.widgets[m.id] = instance;
                        instance.init(shell, { general: this.userSettings[m.id]?.general || {}, layout }, this.hub);
                    }
                };
                document.head.appendChild(s);
            }
        },

        saveSettings: function (widgetId, settings) {
            fetch('/Main/Dashboard/SaveWidgetSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    widgetId: widgetId,
                    generalSettingsJson: JSON.stringify(settings.general || {}),
                    smallScreenSettingsJson: JSON.stringify(settings.small || {}),
                    largeScreenSettingsJson: JSON.stringify(settings.large || {})
                })
            });
        },

        getSet: function (id, sm) {
            const cfg = this.userSettings[id];
            return sm ? (cfg?.small || {}) : (cfg?.large || {});
        },

        /**
         * Full cleanup on navigation
         */
        dispose: function () {
            if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);
            if (this.hub) this.hub.stop();
            Object.values(this.widgets).forEach(w => w.dispose?.());
            this.widgets = {};
        }
    };

    // Auto-boot if the hook is present in the DOM
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) window.ModuleRegistry[SCRIPT_ID].init(hook);
})();