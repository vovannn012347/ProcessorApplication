/**
 * control.js (P2P Module Policy Intercept Control Engine)
 * Handles surgical state management across the P2PMeshControl execution boundaries.
 */
(function () {
    const SCRIPT_ID = "p2p-mesh-control";
    const WRAPPER_ID = "p2p-control-wrapper";

    root = {}
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Mounting policy transaction interception hooks.`);
            this.bindEvents(root);
        },

        bindEvents: function (root) {
            const form = root.querySelector('#applyOverrideForm');
            const token = root.querySelector('input[name="__RequestVerificationToken"]')?.value;

            // Rule Generation Form Submission Block
            if (form) {
                form.onsubmit = (e) => {
                    e.preventDefault();
                    this.submitOverrideRule(root, form);
                };
            }

            // Revoke Rule Event Actions delegation handling
            root.querySelectorAll('.btn-remove-override').forEach(btn => {
                btn.onclick = (e) => {
                    e.preventDefault();
                    this.executeAction(root, 'RemoveOverride', { nodeId: btn.dataset.nodeId }, token);
                };
            });

            // Disruption and Purge actions triggers handler
            const txtNodeInput = root.querySelector('#targetNodeId');

            root.querySelector('#btnDropLaneAction').onclick = (e) => {
                e.preventDefault();
                if (!this.assertNodeId(txtNodeInput)) return;
                this.executeAction(root, 'DropRedundantConnection', { nodeId: txtNodeInput.value }, token, "Confirm out-of-band drop on WebRTC connection lane?");
            };

            root.querySelector('#btnForgetNodeAction').onclick = (e) => {
                e.preventDefault();
                if (!this.assertNodeId(txtNodeInput)) return;
                this.executeAction(root, 'ForgetNode', { nodeId: txtNodeInput.value }, token, "WARNING: This completely purges this node identity from active maps and historical sharded files. Proceed?");
            };
        },

        assertNodeId: function (inputElement) {
            if (!inputElement || !inputElement.value.trim()) {
                alert("Please provide a valid Target NodeId key in the input form field.");
                inputElement?.focus();
                return false;
            }
            return true;
        },

        submitOverrideRule: function (root, form) {
            const btn = root.querySelector('#btnApplyOverride');
            const originalHtml = btn.innerHTML;
            const formData = new FormData(form);

            // Mapping administrative radio configurations into target bool variables expected by endpoint mapping
            const policyVal = form.querySelector('input[name="policyType"]:checked').value;
            formData.append('whitelist', policyVal === 'whitelist');
            formData.append('blacklist', policyVal === 'blacklist');

            btn.disabled = true;
            btn.innerHTML = '<i class="fa-solid fa-circle-notch fa-spin p2p-mr-2"></i> Injecting...';

            fetch(form.action, {
                method: 'POST',
                body: formData,
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.json())
                .then(data => {
                    if (data.success) {
                        this.refreshContainer(root);
                    } else {
                        alert(data.message);
                        btn.disabled = false;
                        btn.innerHTML = originalHtml;
                    }
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Policy addition failure logged:`, err);
                    btn.disabled = false;
                    btn.innerHTML = originalHtml;
                });
        },

        executeAction: function (root, actionName, payload, verificationToken, confirmationMessage = null) {
            if (confirmationMessage && !confirm(confirmationMessage)) return;

            const url = `${window.location.origin}/P2P/MeshControl/${actionName}`;
            const urlEncodedData = new URLSearchParams();

            for (const key in payload) {
                urlEncodedData.append(key, payload[key]);
            }
            if (verificationToken) {
                urlEncodedData.append('__RequestVerificationToken', verificationToken);
            }

            fetch(url, {
                method: 'POST',
                body: urlEncodedData,
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
                .then(res => res.json())
                .then(data => {
                    alert(data.message);
                    if (data.success) {
                        this.refreshContainer(root);
                    }
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Action endpoint execution pipeline failed:`, err));
        },

        refreshContainer: function (root) {
            const currentUrl = `${window.location.origin}/P2P/MeshControl/Index`;
            fetch(currentUrl, {
                method: 'GET',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(res => res.text())
                .then(html => {
                    root.innerHTML = html;
                    this.init(root);
                })
                .catch(err => console.error(`[${SCRIPT_ID}] UI re-indexing lookup collapsed:`, err));
        },

        dispose: function (el) {
            console.log(`[${SCRIPT_ID}] Policy intercept loops detached.`);
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }
})();