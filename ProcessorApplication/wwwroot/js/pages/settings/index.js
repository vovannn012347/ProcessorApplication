(function () {
    const SCRIPT_ID = "main-settings";
    const WRAPPER_ID = "settings-partial-wrapper";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        // Internal state
        activeTab: 'security',
        refreshInterval: null,

        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The specific element with data-script-id
         */
        init: function (el) {
            // Fallback to ID if el isn't provided (for direct loads)
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing on element...`, root);

            this.attachTabListeners(root);
            this.activateTab(root, this.activeTab);
            this.attachFormListener(root);
            this.attachAdminNetworkList(root);

            // Example of a resource that would need disposal
            // this.refreshInterval = setInterval(() => console.log("Settings heartbeat..."), 30000);
        },

        /**
         * Activates a specific tab within the scope of the root element
         */
        activateTab: function (root, tabId) {
            if (!root) return;

            this.activeTab = tabId;
            root.querySelectorAll('.tab-button').forEach(btn => {
                const isActive = btn.getAttribute('data-tab') === tabId;
                btn.classList.toggle('on', isActive);
            });

            root.querySelectorAll('.tab-content').forEach(content => {
                content.classList.toggle('is-hidden', content.id !== `tab-${tabId}`);
            });
        },

        /**
         * Scoped tab listener attachment
         */
        attachTabListeners: function (root) {
            root.querySelectorAll('.tab-button').forEach(btn => {
                btn.onclick = () => this.activateTab(root, btn.dataset.tab);
            });
        },

        /**
         * "Admin access from other computers": add / remove entries. Each entry is a hidden input posted as
         * Security.AdminNetworks; the server checks the format again on save.
         */
        attachAdminNetworkList: function (root) {
            const box = root.querySelector('#admin-networks');
            if (!box) return;
            const rows = box.querySelector('[data-role="admin-network-rows"]');
            const input = box.querySelector('[data-role="admin-network-input"]');
            const error = box.querySelector('[data-role="admin-network-error"]');
            const empty = box.querySelector('[data-role="admin-network-empty"]');
            const prefix = input.dataset.prefix || 'Security';

            const refreshEmpty = () => { empty.hidden = rows.children.length > 0; };
            const looksValid = (v) => /^[0-9a-fA-F:.]+(\/\d{1,3})?$/.test(v) && (v.includes('.') || v.includes(':'));

            const add = () => {
                const value = input.value.trim();
                error.textContent = '';
                if (!value) return;
                if (!looksValid(value)) { error.textContent = 'Not an IP address or network (e.g. 192.168.1.0/24).'; return; }
                const exists = Array.from(rows.querySelectorAll('input[type="hidden"]'))
                    .some(h => h.value.toLowerCase() === value.toLowerCase());
                if (exists) { input.value = ''; return; }

                const tr = document.createElement('tr');
                const cell = document.createElement('td');
                cell.className = 'mono';
                cell.textContent = value;
                const hidden = document.createElement('input');
                hidden.type = 'hidden';
                hidden.name = prefix + '.AdminNetworks';
                hidden.value = value;
                cell.appendChild(hidden);
                const btnCell = document.createElement('td');
                btnCell.style.width = '1%';
                btnCell.innerHTML = '<button type="button" class="icon-btn" data-action="remove-admin-network" title="Remove"><i class="fa-solid fa-xmark"></i></button>';
                tr.appendChild(cell);
                tr.appendChild(btnCell);
                rows.appendChild(tr);
                input.value = '';
                refreshEmpty();
            };

            box.onclick = (e) => {
                const btn = e.target.closest('[data-action]');
                if (!btn) return;
                if (btn.dataset.action === 'add-admin-network') add();
                if (btn.dataset.action === 'remove-admin-network') { btn.closest('tr').remove(); refreshEmpty(); }
            };
            // Enter in the field adds the entry instead of submitting the whole settings form
            input.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); add(); } };
        },

        /**
         * Scoped form listener attachment
         */
        attachFormListener: function (root) {
            const form = root.querySelector('form'); // Finds the masterSettingsForm within this root
            if (!form) return;

            form.onsubmit = (e) => {
                e.preventDefault();
                this.handleSave(root, form);
            };
        },

        /**
         * AJAX Form Handling
         */
        handleSave: function (root, form) {
            const btn = root.querySelector('#masterSaveButton');
            const originalHtml = btn.innerHTML;
            btn.innerHTML = '<i class="fa-solid fa-sync fa-spin"></i> Saving...';
            btn.disabled = true;

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.text())
                .then(html => {
                    // Since we are inside an AJAX-loaded partial, we update the root element's content
                    root.innerHTML = html;

                    // Re-initialize logic on the updated internal DOM of this root
                    this.init(root);
                })
                .catch(err => {
                    btn.innerHTML = originalHtml;
                    btn.disabled = false;
                    alert("Save failed: " + err.message);
                });
        },

        /**
         * THE UNLOADING HOOK
         * @param {HTMLElement} el - The element being removed
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Unloading element:`, el);

            // Commented sample of disposal logic:
            /*
            if (this.refreshInterval) {
                clearInterval(this.refreshInterval);
                this.refreshInterval = null;
            }
            */
        }
    };

    // If this is a direct load (non-AJAX), launch immediately
    const existing = document.getElementById(WRAPPER_ID);
    if (existing) {
        window.ModuleRegistry[SCRIPT_ID].init(existing);
    }

    //# sourceURL=js/modules/main-settings.js
})();