/**
 * home-index.js (Providerless Module)
 * Handles the Clinic Gateway Orchestrator UI with Element Hooks.
 */
(function () {
    const SCRIPT_ID = "providerless-home";
    const CONFIG = {
        pollInterval: 5000,
        endpoints: {
            status: '/Providerless/Home/GetStatus',
            reestablish: '/Providerless/Home/Reestablish'
        }
    };

    let root = {}; // local to this script (was an implicit global shared by all page scripts)

    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        poller: null,
        qrcode: null,
        lastUrl: "",

        /**
         * Lifecycle Init
         * @param {HTMLElement} el - The providerless-home-wrapper hooked via data-script-id
         */
        init: function (el) {
            root = el || document.getElementById('providerless-home-wrapper');
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Initializing gateway orchestrator...`);

            // Initialize QR Code inside the scoped root
            const qrContainer = root.querySelector('#qrcode');
            if (qrContainer && typeof QRCode !== 'undefined') {
                this.qrcode = new QRCode(qrContainer, {
                    width: 256,
                    height: 256,
                    colorDark: "#0f172a",
                    colorLight: "#ffffff",
                    correctLevel: QRCode.CorrectLevel.H
                });
            }

            // Start polling
            this.poller = setInterval(() => this.updateStatus(root), CONFIG.pollInterval);
            this.updateStatus(root);

            // Attach Administrative Click Listeners if they exist
            const reestablishBtn = root.querySelector('#reestablish-btn');
            if (reestablishBtn) {
                reestablishBtn.onclick = () => this.reestablishConnection(root);
            }
        },

        updateStatus: async function (root) {
            if (!root) return;

            try {
                const response = await fetch(CONFIG.endpoints.status, {
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                });
                if (!response.ok) throw new Error("Status endpoint unavailable");

                const data = await response.json();

                // 1. Update Status Badge
                const badge = root.querySelector('#status-badge');
                if (badge) {
                    const isOnline = data.tunnelActive && data.registyActive;
                    badge.innerText = isOnline ? "Online & Discovery Active" : (data.tunnelActive ? "Tunneling Active" : "Offline");
                    badge.className = isOnline
                        ? "badge ok"
                        : "badge warn";
                }

                // 2. Update Stats
                const updateText = (id, text, colorClass = "") => {
                    const target = root.querySelector(`#${id}`);
                    if (target) {
                        target.innerText = text || "--";
                        if (colorClass) target.className = colorClass;
                    }
                };

                updateText('stat-provider', data.providerName);
                updateText('stat-start', data.startTime);
                updateText('stat-tunnel', data.tunnelActive ? "RUNNING" : "STOPPED",
                    data.tunnelActive ? "ok-t" : "bad-t");
                if (!data.registryEnabled) {
                    updateText('stat-registry', "OFF", "aux");
                } else {
                    updateText('stat-registry', data.registyActive ? "SYNCED" : "PENDING",
                        data.registyActive ? "ok-t" : "warn-t");
                }

                // URLs: tunnel, resolver link, what the registry holds, the registry entry itself
                const setLink = (id, url) => {
                    const a = root.querySelector(`#${id}`);
                    if (!a) return;
                    if (url) { a.innerText = url; a.href = url; }
                    else { a.innerText = "--"; a.removeAttribute('href'); }
                };
                setLink('url-tunnel', data.tunnelUrl);
                setLink('url-public', data.publicUrl);
                setLink('url-registered', data.registeredUrl);
                setLink('url-registry-entry', data.registryEntryUrl);

                // Error from the last start attempt
                const errBox = root.querySelector('#error-container');
                const errText = root.querySelector('#error-text');
                if (errBox && errText) {
                    errText.innerText = data.lastError || "";
                    errBox.classList.toggle('is-hidden', !data.lastError);
                }

                // 3. Visibility Logic: QR = public URL once registered, the tunnel URL until then
                const activeSec = root.querySelector('#active-section');
                const inactiveSec = root.querySelector('#inactive-section');

                if (data.tunnelActive && data.url) {
                    activeSec?.classList.remove('is-hidden');
                    inactiveSec?.classList.add('is-hidden');

                    const caption = root.querySelector('#qr-caption');
                    if (caption) caption.innerText = data.publicUrl ? "QR: Public URL" : "QR: Tunnel URL (no public URL yet)";

                    if (this.qrcode && data.url && data.url !== this.lastUrl) {
                        this.lastUrl = data.url;
                        this.qrcode.clear();
                        this.qrcode.makeCode(data.url);
                    }
                } else {
                    activeSec?.classList.add('is-hidden');
                    inactiveSec?.classList.remove('is-hidden');
                }

            } catch (e) {
                console.warn(`[${SCRIPT_ID}] Polling error:`, e);
            }
        },

        reestablishConnection: async function (root) {
            const btn = root.querySelector('#reestablish-btn');
            const force = root.querySelector('#reestablish-now')?.checked;

            if (!force) {
                alert("Please check the confirmation box to force restart.");
                return;
            }

            btn.disabled = true;
            const originalText = btn.innerHTML;
            btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin"></i> Signaling...';

            try {
                const res = await fetch(CONFIG.endpoints.reestablish, {
                    method: 'POST',
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                });
                const result = await res.json();
                if (result.success) {
                    this.lastUrl = "";
                    setTimeout(() => this.updateStatus(root), 1500);
                }
            } finally {
                btn.disabled = false;
                btn.innerHTML = originalText;
            }
        },

        printPatientHandout: function () {
            // This logic uses the specific lastUrl stored in the instance
            const url = this.lastUrl;
            const template = document.getElementById("patient-print-template");
            const printTarget = document.getElementById("print-qr-target");

            if (!url || !template || !printTarget) return;

            printTarget.innerHTML = "";
            new QRCode(printTarget, {
                text: url,
                width: 256,
                height: 256,
                colorDark: "#000000"
            });

            setTimeout(() => {
                const urlText = document.getElementById("print-url-text");
                if (urlText) urlText.innerText = url;

                const printWin = window.open('', '_blank', 'width=800,height=900');
                const styles = Array.from(document.querySelectorAll('link[rel="stylesheet"]'))
                    .map(s => s.outerHTML).join('');

                printWin.document.write(`
                    <html>
                        <head>
                            <title>Patient Handout</title>
                            ${styles}
                            <style>
                                body { background: white; padding: 20px; }
                                #patient-print-template { display: block !important; visibility: visible !important; transform: scale(1.25); transform-origin: top center; margin-top: 20mm; }
                                .is-hidden { display: block !important; visibility: visible !important; }
                                #print-qr-target img { margin: 0 auto; display: block; }
                            </style>
                        </head>
                        <body>${template.innerHTML}</body>
                    </html>
                `);

                printWin.document.close();
                setTimeout(() => {
                    printWin.print();
                }, 500);
            }, 150);
        },

        downloadQRCode: function () {
            const canvas = document.querySelector("#qrcode canvas");
            if (!canvas) return;
            const link = document.createElement('a');
            link.download = `portal-access-qr.png`;
            link.href = canvas.toDataURL("image/png");
            link.click();
        },

        copyImageToClipboard: function () {
            const canvas = document.querySelector("#qrcode canvas");
            if (!canvas) return;
            canvas.toBlob(blob => {
                const item = new ClipboardItem({ "image/png": blob });
                navigator.clipboard.write([item]).then(() => alert("QR Code copied to clipboard!"));
            });
        },

        /**
         * THE UNLOADING HOOK
         * @param {HTMLElement} el - The element being removed
         */
        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Disposing lifecycle... stopping poller.`);
            if (this.poller) {
                clearInterval(this.poller);
                this.poller = null;
            }
        }
    };

    // SELF-LAUNCHING BLOCK
    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }

    // Expose print/download to global scope for the inline onclick handlers
    window.Providerless = window.ModuleRegistry[SCRIPT_ID];

    //# sourceURL=js/modules/providerless/home-index.js
})();