(function () {
    const SCRIPT_ID = "main-settings-ingest";
    const WRAPPER_ID = "settings-ingest-wrapper";

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        init: function (el) {
            const root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            root.querySelectorAll('form.ingest-form').forEach(form => {
                form.onsubmit = (e) => {
                    e.preventDefault();
                    const question = form.dataset.confirm;
                    if (question && !window.confirm(question)) return;
                    this.submit(root, form);
                };
            });
        },

        submit: function (root, form) {
            const btn = form.querySelector('button[type="submit"]');
            if (btn) {
                btn.disabled = true;
                btn.innerHTML = '<i class="fa-solid fa-sync fa-spin mm-mr-2"></i> Applying...';
            }

            fetch(form.action, {
                method: 'POST',
                body: new FormData(form),
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => {
                    if (res.status === 401) { window.location.reload(); return null; }
                    if (!res.ok) throw new Error(`HTTP ${res.status}`);
                    return res.text();
                })
                .then(html => {
                    if (html === null) return;
                    // The response is this page again (partial); swap the wrapper's content and re-bind.
                    const doc = new DOMParser().parseFromString(html, 'text/html');
                    const fresh = doc.getElementById(WRAPPER_ID);
                    root.innerHTML = fresh ? fresh.innerHTML : html;
                    this.init(root);
                })
                .catch(err => {
                    if (btn) btn.disabled = false;
                    alert("Applying settings failed: " + err.message);
                });
        },

        dispose: function (el) { }
    };

    const existing = document.getElementById(WRAPPER_ID);
    if (existing) {
        window.ModuleRegistry[SCRIPT_ID].init(existing);
    }

    //# sourceURL=js/modules/main-settings-ingest.js
})();
