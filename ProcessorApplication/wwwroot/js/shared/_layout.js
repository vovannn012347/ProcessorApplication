/*
  some causal javascript OOP
 */
(function () {
    let container, loader, contextualMenu;

    document.addEventListener('DOMContentLoaded', function () {
        container = document.getElementById('app-container');
        loader = document.getElementById('content-loader');
        contextualMenu = document.getElementById('contextual-menu-container');

        // --- Sidebar & Navigation Toggles ---
        const openBtn = document.getElementById('sidebar-open-btn');
        const closeBtn = document.getElementById('sidebar-close-btn');
        const backdrop = document.getElementById('sidebar-backdrop');
        const toggleBtn = document.getElementById('sidebar-toggle-btn');
        const toggleIcon = document.getElementById('sidebar-toggle-icon');

        if (openBtn) openBtn.onclick = () => container.classList.add('mm-sidebar-open');
        if (closeBtn) closeBtn.onclick = () => container.classList.remove('mm-sidebar-open');
        if (backdrop) backdrop.onclick = () => container.classList.remove('mm-sidebar-open');

        if (toggleBtn) {
            toggleBtn.onclick = () => {
                const isCol = container.classList.toggle('mm-sidebar-collapsed');
                if (toggleIcon) toggleIcon.className = isCol ? 'fa-solid fa-angles-right' : 'fa-solid fa-angles-left';
            };
        }

        // --- User Menu ---
        const uBtn = document.getElementById('user-menu-button');
        const uDrop = document.getElementById('user-dropdown');
        const uArr = document.getElementById('user-dropdown-arrow');
        if (uBtn) {
            uBtn.onclick = (e) => {
                e.stopPropagation();
                const hid = uDrop.classList.toggle('is-hidden');
                if (uArr) uArr.style.transform = hid ? 'rotate(0deg)' : 'rotate(180deg)';
            };
        }
        window.onclick = () => {
            uDrop?.classList.add('is-hidden');
            if (uArr) uArr.style.transform = 'rotate(0deg)';
        };

        // --- (i) hints: <span class="tip" tabindex="0" data-tip="text">. One floating box, shown above the
        //     hint (below when there is no room), kept inside the window. Hover, or tap/focus on touch screens.
        const tipBox = document.createElement('div');
        tipBox.id = 'tip-box';
        document.body.appendChild(tipBox);
        const tipOf = e => e.target.closest ? e.target.closest('.tip') : null;
        const showTip = t => {
            tipBox.textContent = t.dataset.tip || '';
            tipBox.style.display = 'block';
            const r = t.getBoundingClientRect(), b = tipBox.getBoundingClientRect();
            const top = r.top - b.height - 4 < 2 ? r.bottom + 4 : r.top - b.height - 4;
            tipBox.style.top = top + 'px';
            tipBox.style.left = Math.max(2, Math.min(r.left - 4, window.innerWidth - b.width - 2)) + 'px';
        };
        const hideTip = () => { tipBox.style.display = 'none'; };
        document.addEventListener('mouseover', e => { const t = tipOf(e); if (t) showTip(t); });
        document.addEventListener('mouseout', e => { if (tipOf(e)) hideTip(); });
        document.addEventListener('focusin', e => { const t = tipOf(e); if (t) showTip(t); });
        document.addEventListener('focusout', e => { if (tipOf(e)) hideTip(); });
        window.addEventListener('scroll', hideTip, true);
        // a tap on a hint inside a <label> must not toggle the label's checkbox/radio
        document.addEventListener('click', e => { if (tipOf(e)) e.preventDefault(); });

        // --- Module Initialization ---
        fetch('/Main/Navigation/GetModules')
            .then(res => res.json())
            .then(modules => buildModuleNav(modules));

        // Expose loadContent globally
        window.loadContent = (url) => loadContent(url, document.getElementById('content-container'));
    });

    /**
     * Core content loading with asset management and element hooks
     */
    function loadContent(url, target, callback) {
        if (target.id === 'content-container' && loader) loader.classList.remove('is-hidden');

        // 1. DISPOSE PHASE: Trigger cleanup on elements being removed
        target.querySelectorAll('[data-script-id]').forEach(el => {
            const id = el.dataset.scriptId;
            if (window.ModuleRegistry && window.ModuleRegistry[id]?.dispose) {
                // Pass the element itself to the dispose hook
                window.ModuleRegistry[id].dispose(el);
            }
        });

        fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
            .then(res => res.status === 401 ? window.location.reload() : res.text())
            .then(html => {
                if (target.id === 'content-container' && loader) loader.classList.add('is-hidden');

                const parser = new DOMParser();
                const doc = parser.parseFromString(html, 'text/html');

                const incomingLinks = Array.from(doc.querySelectorAll('link[rel="stylesheet"]'));
                const incomingScripts = Array.from(doc.querySelectorAll('script'));

                incomingLinks.forEach(l => l.remove());
                incomingScripts.forEach(s => s.remove());

                target.innerHTML = doc.body.innerHTML;

                // 2. INIT PHASE: Trigger initialization for new elements
                target.querySelectorAll('[data-script-id]').forEach(el => {
                    const id = el.dataset.scriptId;
                    if (window.ModuleRegistry && window.ModuleRegistry[id]?.init) {
                        // Pass the element itself to the init hook
                        window.ModuleRegistry[id].init(el);
                    }
                });

                // 3. Manage CSS (Load Once)
                incomingLinks.forEach(l => {
                    const href = l.getAttribute('href');
                    if (!document.querySelector(`link[href="${href}"]`)) {
                        const nl = document.createElement('link');
                        nl.rel = 'stylesheet';
                        nl.href = href;
                        nl.className = 'module-asset';
                        document.head.appendChild(nl);
                    }
                });

                // 4. Manage JS (Load Once or Debug-Exec)
                incomingScripts.forEach(s => {
                    if (s.src) {
                        if (!document.querySelector(`script[src="${s.src}"]`)) {
                            const ns = document.createElement('script');
                            ns.src = s.src;
                            ns.className = 'module-asset';
                            ns.async = false;
                            document.head.appendChild(ns);
                        }
                    } else {
                        const ns = document.createElement('script');
                        const debugName = s.dataset.debugName || `ajax-exec-${Date.now()}`;
                        ns.textContent = s.textContent + `\n//# sourceURL=dynamic/${debugName}.js`;
                        document.head.appendChild(ns);
                        ns.remove();
                    }
                });

                if (callback) callback();

                if (window.innerWidth < 768 && target.id === 'content-container') {
                    container.classList.remove('mm-sidebar-open');
                }
            })
            .catch(err => {
                console.error("Navigation Error:", err);
                if (loader) loader.classList.add('is-hidden');
            });
    }

    function buildModuleNav(modules) {
        const desk = document.getElementById('desktop-module-nav-container');
        const mob = document.getElementById('mobile-module-nav-container');
        const tplDesk = document.getElementById('tpl-nav-desktop');
        const tplMob = document.getElementById('tpl-nav-mobile');

        if (!desk || !mob || !tplDesk || !tplMob) return;

        const currentPath = window.location.pathname.toLowerCase();
        let activeId = modules[0]?.moduleId || 'main';

        modules.forEach(m => {
            if (currentPath.includes(m.moduleId.toLowerCase())) activeId = m.moduleId;

            const dNode = tplDesk.content.cloneNode(true).querySelector('a');
            dNode.textContent = m.name;
            dNode.dataset.mid = m.moduleId;
            dNode.onclick = (e) => { e.preventDefault(); switchModule(m.moduleId); };
            desk.appendChild(dNode);

            const mNode = tplMob.content.cloneNode(true).querySelector('a');
            mNode.querySelector('.label').textContent = m.name;
            mNode.dataset.mid = m.moduleId;
            mNode.onclick = (e) => { e.preventDefault(); switchModule(m.moduleId); };
            mob.appendChild(mNode);
        });

        switchModule(activeId, true);
    }

    function switchModule(id, isInitial = false) {
        document.querySelectorAll('.module-nav-link').forEach(l => {
            const isActive = l.dataset.mid === id;
            l.classList.toggle('on', isActive);
        });

        loadContent(`/Main/Navigation/GetModuleMenu?moduleId=${id}`, contextualMenu, markCurrentPage);
    }

    /**
     * Marks the menu link of the current page and, when the page set no ViewData["Title"],
     * shows that link's text as the page name in the sidebar head.
     */
    function markCurrentPage() {
        const path = window.location.pathname.toLowerCase().replace(/\/+$/, '');
        let best = null, bestLen = -1;
        contextualMenu.querySelectorAll('a[href]').forEach(a => {
            const href = new URL(a.getAttribute('href'), window.location.origin).pathname.toLowerCase().replace(/\/+$/, '');
            const match = path === href || path.startsWith(href + '/') || (path + '/index') === href;
            if (match && href.length > bestLen) { best = a; bestLen = href.length; }
            a.classList.remove('on');
        });
        if (!best) return;
        best.classList.add('on');
        const title = document.getElementById('page-title');
        if (title && !title.textContent.trim()) {
            title.textContent = best.textContent.trim();
            document.title = title.textContent + ' - ProcessorApplication';
        }
    }
})();