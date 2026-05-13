/**
 * main-dashboard.js
 * Orchestrates real-time telemetry and widget updates for the Core Hub.
 */
(function () {
    const SCRIPT_ID = "main-dashboard";
    const WRAPPER_ID = "dashboard-hub-wrapper";
    const HEARTBEAT_INTERVAL = 20000; // 20 seconds

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        hub: null,
        catalog: [],
        userSettings: {},
        widgets: {},

        init: async function (el) {
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            try {
                // STEP 1: Get Catalog
                const catalogRes = await fetch('/Main/Dashboard/GetCatalog');
                this.catalog = await catalogRes.json();

                // STEP 2: Link SignalR and Load Settings for Catalog IDs
                await this.initSession();

                // STEP 3: Render
                this.renderDashboard(root);

                // STEP 3+: Stay alive
                this.startHeartbeatLoop();

            } catch (err) {
                console.error("[Dashboard] Init failed:", err);
            }
        },

        initSession: async function () {
            // Request settings only for IDs present in catalog
            const ids = this.catalog.map(m => m.id);
            const query = ids.map(id => `ids=${encodeURIComponent(id)}`).join('&');

            const settingsRes = await fetch(`/Main/Dashboard/GetUserSettings?${query}`);
            this.userSettings = await settingsRes.json();

            this.hub = new signalR.HubConnectionBuilder()
                .withUrl("/dashboardHub")
                .withAutomaticReconnect()
                .build();

            this.hub.on("ReceiveData", (widgetId, payload) => {
                const instance = this.widgets[widgetId];
                if (instance && instance.updateUI) instance.updateUI(payload);
            });

            await this.hub.start();
        },

        renderDashboard: function (root) {
            const container = root.querySelector('#widget-grid-container');
            if (!container) return;
            container.innerHTML = '';

            const isSmall = window.innerWidth < 1024;
            const sorted = [...this.catalog].sort((a, b) => {
                const orderA = this.getSetting(a.id, isSmall).order || a.defaultOrder;
                const orderB = this.getSetting(b.id, isSmall).order || b.defaultOrder;
                return orderA - orderB;
            });

            sorted.forEach(m => {
                const setting = this.getSetting(m.id, isSmall);
                if (setting.isHidden) return;

                const shellFragment = this.createShell(m, setting);
                const shellElement = shellFragment.querySelector('.widget-shell');
                container.appendChild(shellFragment);

                if (!setting.isCollapsed) {
                    this.bootWidget(m, shellElement, setting);
                } else {
                    shellElement.querySelector('.widget-body').classList.add('mm-hidden');
                }
            });
        },

        startHeartbeatLoop: function () {
            if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);

            this.heartbeatTimer = setInterval(() => {
                if (this.hub && this.hub.state === signalR.HubConnectionState.Connected) {
                    this.hub.invoke("Heartbeat").catch(err => console.warn("Heartbeat failed", err));
                }
            }, HEARTBEAT_INTERVAL);
        },

        bootWidget: async function (m, shell, layoutSetting) {
            const body = shell.querySelector('.widget-body');

            if (this.hub && this.hub.state === "Connected") {
                this.hub.invoke("ActivateWidget", m.id);
            }

            const res = await fetch(`/Main/Dashboard/GetWidgetView?widgetId=${m.id}`);
            if (!res.ok) { body.innerHTML = "Access Denied"; return; }

            body.innerHTML = await res.text();

            if (m.scriptPath) {
                const versionedPath = `${m.scriptPath}?v=${new Date().getTime()}`;

                const s = document.createElement('script');
                s.src = versionedPath;
                s.onload = () => {
                    const logic = window.WidgetLibrary?.[m.id];
                    if (logic) {
                        const instance = Object.create(logic);
                        this.widgets[m.id] = instance;
                        instance.init(shell, { general: this.userSettings[m.id]?.general || {}, layoutSetting }, this.hub);
                    }
                };
                document.head.appendChild(s);
            }
        },

        saveWidgetSettings: async function (widgetId, general, small, large) {
            const payload = {
                WidgetId: widgetId,
                GeneralSettingsJson: JSON.stringify(general),
                SmallScreenSettingsJson: JSON.stringify(small),
                LargeScreenSettingsJson: JSON.stringify(large)
            };

            await fetch('/Main/Dashboard/SaveWidgetSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
        },

        getSetting: function (id, isSmall) {
            const cfg = this.userSettings[id];
            return isSmall ? (cfg?.small || {}) : (cfg?.large || {});
        },

        createShell: function (m, setting) {
            const template = document.getElementById('widget-shell-template');
            const clone = template.content.cloneNode(true);
            const shell = clone.querySelector('.widget-shell');

            const width = setting.width || 1;
            const widthClass = width > 1 ? `lg:mm-col-span-${width} md:mm-col-span-2` : 'mm-col-span-1';

            widthClass.split(' ').forEach(cls => shell.classList.add(cls));
            shell.style.order = setting.order || m.defaultOrder;
            shell.dataset.widgetId = m.id;

            const icon = shell.querySelector('.widget-icon');
            if (icon && m.iconClass) {
                m.iconClass.split(' ').forEach(cls => icon.classList.add(cls));
            }

            const title = shell.querySelector('.widget-title');
            if (title) title.innerText = m.name;

            shell.querySelector('.widget-header').onclick = () => {
                const body = shell.querySelector('.widget-body');
                const isCollapsed = body.classList.toggle('mm-hidden');
                // Persistence logic would update local setting and call saveWidgetSettings
            };

            return clone;
        },

        loadScript: function (src, callback) {
            if (document.querySelector(`script[src^="${src}"]`)) return callback();
            const s = document.createElement('script');
            s.src = src;
            s.onload = callback;
            document.head.appendChild(s);
        },

        dispose: function () {
            if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);
            if (this.hub) {
                this.hub.stop();
                this.hub = null;
            }
            Object.values(this.widgets).forEach(w => w.dispose && w.dispose());
            this.widgets = {};
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) window.ModuleRegistry[SCRIPT_ID].init(hook);
})();