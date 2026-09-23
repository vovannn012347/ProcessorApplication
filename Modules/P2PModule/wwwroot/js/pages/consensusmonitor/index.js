/**
 * consensus-monitor.js (P2P Consensus Monitor Engineering Control Script)
 * Orchestrates tracking queries across sharded date boundaries over isolation walls.
 * Controls Tab 1 (Active Live Session) and Tab 2 (Historical Ledger Audit).
 */
(function () {
    const SCRIPT_ID = "p2p-consensus-monitor";
    const WRAPPER_ID = "p2p-consensus-wrapper";
    const CONTROLLER_PATH = "P2P/HistoryMonitor";

    let root = {};
    window.ModuleRegistry = window.ModuleRegistry || {};

    window.ModuleRegistry[SCRIPT_ID] = {
        pollTimer: null,

        init: function (el) {
            root = el || document.getElementById(WRAPPER_ID);
            if (!root) return;

            console.log(`[${SCRIPT_ID}] Binding Tabbed Consensus Monitor.`);
            this.bindEvents(root);
            this.startLiveSessionPolling(root);
        },

        bindEvents: function (rootElement) {
            const token = rootElement.querySelector('input[name="__RequestVerificationToken"]')?.value;

            // Tab Navigation Switcher
            rootElement.querySelectorAll('.nav-tab-btn').forEach(btn => {
                btn.onclick = (e) => {
                    const targetTab = e.currentTarget.dataset.tab;
                    rootElement.querySelectorAll('.nav-tab-btn').forEach(t => {
                        t.classList.remove('p2p-border-blue-600', 'p2p-text-blue-600');
                        t.classList.add('p2p-border-transparent', 'p2p-text-slate-500');
                    });
                    e.currentTarget.classList.remove('p2p-border-transparent', 'p2p-text-slate-500');
                    e.currentTarget.classList.add('p2p-border-blue-600', 'p2p-text-blue-600');

                    rootElement.querySelectorAll('.tab-content').forEach(c => c.classList.add('p2p-hidden'));
                    const targetContent = rootElement.querySelector(`#tab-${targetTab}`);
                    if (targetContent) targetContent.classList.remove('p2p-hidden');

                    if (targetTab === 'active-session') {
                        this.startLiveSessionPolling(rootElement);
                    } else {
                        this.stopLiveSessionPolling();
                    }
                };
            });

            // Action: Trigger Manual Consensus
            const btnTrigger = rootElement.querySelector('#btn-trigger-consensus');
            if (btnTrigger) {
                btnTrigger.onclick = () => {
                    this.executePostAction('TriggerManualConsensus', {}, token, "Initialize manual consensus merge session loop?", () => {
                        this.fetchActiveSessionState(rootElement);
                    });
                };
            }

            const btnAbort = rootElement.querySelector('#btn-abort-consensus');
            if (btnAbort) {
                btnAbort.onclick = () => {
                    this.executePostAction('AbortConsensus', {}, token, "Force immediate abort and purge active consensus session workspace?", () => {
                        this.fetchActiveSessionState(rootElement);
                    });
                };
            }

            // Action: Force Health Recalculation
            const btnHealth = rootElement.querySelector('#btn-force-health-check');
            if (btnHealth) {
                btnHealth.onclick = () => {
                    this.executePostAction('ForceHealthRecalculation', {}, token, null, () => {
                        alert("Cryptographic baseline audit executed.");
                    });
                };
            }

            // Partition Click Handler (Tab 2)
            rootElement.querySelectorAll('.partition-date-item').forEach(item => {
                item.onclick = (e) => {
                    const dateKey = e.currentTarget.dataset.dateKey;

                    rootElement.querySelectorAll('.partition-date-item').forEach(i => i.classList.remove('p2p-bg-slate-100', 'p2p-border-blue-500'));
                    e.currentTarget.classList.add('p2p-bg-slate-100', 'p2p-border-blue-500');

                    const emptyState = rootElement.querySelector('#emptyDetailsState');
                    const contentPanel = rootElement.querySelector('#detailsContentPanel');
                    const lblDate = rootElement.querySelector('#lblSelectedDate');

                    if (emptyState) emptyState.classList.add('p2p-hidden');
                    if (contentPanel) contentPanel.classList.remove('p2p-hidden');
                    if (lblDate) lblDate.textContent = dateKey;

                    this.fetchPartitionNodes(rootElement, dateKey);
                };
            });

            // Global Event Delegation for Historical Node Details Expansion
            rootElement.addEventListener('click', (e) => {
                const headerRow = e.target.closest('.node-header-row');
                if (!headerRow) return;

                const accordionItem = headerRow.closest('.node-accordion-item');
                if (!accordionItem) return;

                const nodeId = accordionItem.dataset.nodeId;
                const targetDate = accordionItem.dataset.targetDate;
                const drawer = accordionItem.querySelector('.node-details-drawer');
                const chevron = accordionItem.querySelector('.node-chevron');

                if (!drawer) return;

                const isOpening = drawer.classList.contains('p2p-hidden');
                drawer.classList.toggle('p2p-hidden');
                if (chevron) {
                    chevron.classList.toggle('p2p-rotate-90');
                }

                if (isOpening) {
                    this.fetchHistoricalNodeDetails(accordionItem, targetDate, nodeId);
                }
            });
        },

        startLiveSessionPolling: function (rootElement) {
            this.stopLiveSessionPolling();
            this.fetchActiveSessionState(rootElement);
            this.pollTimer = setInterval(() => this.fetchActiveSessionState(rootElement), 5000);
        },

        stopLiveSessionPolling: function () {
            if (this.pollTimer) {
                clearInterval(this.pollTimer);
                this.pollTimer = null;
            }
        },

        fetchActiveSessionState: function (rootElement) {
            const url = `${window.location.origin}/${CONTROLLER_PATH}/GetActiveSessionState`;
            fetch(url)
                .then(res => res.json())
                .then(data => {
                    const statusBadge = rootElement.querySelector('#sessionStatusBadge');
                    const lblSessionId = rootElement.querySelector('#lblSessionId');
                    const lblTargetDate = rootElement.querySelector('#lblTargetDate');
                    const lblInitiator = rootElement.querySelector('#lblInitiator');
                    const lblTargetNode = rootElement.querySelector('#lblTargetNode');
                    const lblLastActivity = rootElement.querySelector('#lblLastActivity');
                    const containerVisited = rootElement.querySelector('#containerVisitedNodes');
                    const containerQueued = rootElement.querySelector('#containerQueuedNodes');
                    const countVisited = rootElement.querySelector('#lblVisitedCount');
                    const countQueued = rootElement.querySelector('#lblQueuedCount');

                    if (statusBadge) {
                        if (data.isBusy) {
                            statusBadge.textContent = "ACTIVE MERGE SESSION";
                            statusBadge.className = "p2p-px-2.5 p2p-py-1 p2p-text-xs p2p-font-bold p2p-rounded-full p2p-bg-emerald-100 p2p-text-emerald-700";
                        } else {
                            statusBadge.textContent = "IDLE / STANDBY";
                            statusBadge.className = "p2p-px-2.5 p2p-py-1 p2p-text-xs p2p-font-bold p2p-rounded-full p2p-bg-slate-100 p2p-text-slate-600";
                        }
                    }

                    if (lblSessionId) lblSessionId.textContent = data.sessionId || "None";
                    if (lblTargetDate) lblTargetDate.textContent = data.targetDate || "N/A";
                    if (lblInitiator) lblInitiator.textContent = data.initiator || "N/A";
                    if (lblTargetNode) lblTargetNode.textContent = data.targetNode || "None (Broadcasting)";
                    if (lblLastActivity) lblLastActivity.textContent = data.lastActivity ? new Date(data.lastActivity).toLocaleTimeString() : "N/A";
                    if (countVisited) countVisited.textContent = data.visitedCount || 0;
                    if (countQueued) countQueued.textContent = data.queuedCount || 0;

                    // Render Visited Nodes using Template Cloning (NO RAW HTML)
                    if (containerVisited) {
                        containerVisited.textContent = '';
                        if (data.visitedNodes && data.visitedNodes.length > 0) {
                            const tmpl = document.getElementById('tmpl-node-tag');
                            data.visitedNodes.forEach(nodeId => {
                                if (tmpl) {
                                    const clone = tmpl.content.cloneNode(true);
                                    const tag = clone.querySelector('.node-tag-text');
                                    if (tag) tag.textContent = nodeId;
                                    containerVisited.appendChild(clone);
                                }
                            });
                        } else {
                            const tmplEmpty = document.getElementById('tmpl-empty-info');
                            if (tmplEmpty) {
                                const clone = tmplEmpty.content.cloneNode(true);
                                const msg = clone.querySelector('.msg-empty');
                                if (msg) msg.textContent = 'No visited nodes recorded yet.';
                                containerVisited.appendChild(clone);
                            }
                        }
                    }

                    // Render Queued Nodes using Template Cloning (NO RAW HTML)
                    if (containerQueued) {
                        containerQueued.textContent = '';
                        if (data.queuedNodes && data.queuedNodes.length > 0) {
                            const tmpl = document.getElementById('tmpl-node-tag');
                            data.queuedNodes.forEach(nodeId => {
                                if (tmpl) {
                                    const clone = tmpl.content.cloneNode(true);
                                    const tag = clone.querySelector('.node-tag-text');
                                    if (tag) tag.textContent = nodeId;
                                    containerQueued.appendChild(clone);
                                }
                            });
                        } else {
                            const tmplEmpty = document.getElementById('tmpl-empty-info');
                            if (tmplEmpty) {
                                const clone = tmplEmpty.content.cloneNode(true);
                                const msg = clone.querySelector('.msg-empty');
                                if (msg) msg.textContent = 'Visit queue is empty.';
                                containerQueued.appendChild(clone);
                            }
                        }
                    }
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Error fetching session state:`, err));
        },

        fetchPartitionNodes: function (rootElement, dateKey) {
            const displayBox = rootElement.querySelector('#partitionDisplayBox');
            if (!displayBox) return;

            displayBox.textContent = '';
            const tmplSpinner = document.getElementById('tmpl-loading-spinner');
            if (tmplSpinner) {
                const clone = tmplSpinner.content.cloneNode(true);
                const msg = clone.querySelector('.msg-loading');
                if (msg) msg.textContent = 'Loading partition entries...';
                displayBox.appendChild(clone);
            }

            const url = `${window.location.origin}/${CONTROLLER_PATH}/GetStagingNodesList?dateKey=${encodeURIComponent(dateKey)}`;
            fetch(url)
                .then(res => res.text())
                .then(html => displayBox.innerHTML = html)
                .catch(() => {
                    displayBox.textContent = '';
                    const tmplErr = document.getElementById('tmpl-error-message');
                    if (tmplErr) {
                        const clone = tmplErr.content.cloneNode(true);
                        const msg = clone.querySelector('.msg-error');
                        if (msg) msg.textContent = 'Failed to load partition records.';
                        displayBox.appendChild(clone);
                    }
                });
        },

        fetchHistoricalNodeDetails: function (accordionItem, dateKey, nodeId) {
            const drawerLoading = accordionItem.querySelector('.drawer-loading');
            const drawerContent = accordionItem.querySelector('.drawer-content');

            if (drawerLoading) drawerLoading.classList.remove('p2p-hidden');
            if (drawerContent) drawerContent.classList.add('p2p-hidden');

            const url = `${window.location.origin}/${CONTROLLER_PATH}/GetHistoricalNodeDetails?date=${encodeURIComponent(dateKey)}&nodeId=${encodeURIComponent(nodeId)}`;
            fetch(url)
                .then(res => {
                    if (!res.ok) throw new Error(`HTTP ${res.status}`);
                    return res.json();
                })
                .then(data => {
                    const badgeVerification = drawerContent.querySelector('.badge-verification');
                    const lblTrustScore = drawerContent.querySelector('.lbl-trust-score');
                    const lblLastSeen = drawerContent.querySelector('.lbl-last-seen');
                    const lblSignatureTime = drawerContent.querySelector('.lbl-signature-time');
                    const containerRoles = drawerContent.querySelector('.container-roles');
                    const containerEndpoints = drawerContent.querySelector('.container-endpoints');

                    if (badgeVerification) {
                        if (data.isVerified) {
                            badgeVerification.textContent = "VERIFIED IDENTITY";
                            badgeVerification.className = "badge-verification p2p-font-bold p2p-text-xs p2p-px-2 p2p-py-0.5 p2p-rounded p2p-bg-emerald-50 p2p-text-emerald-700 p2p-border p2p-border-emerald-200";
                        } else {
                            badgeVerification.textContent = "UNVERIFIED IDENTITY";
                            badgeVerification.className = "badge-verification p2p-font-bold p2p-text-xs p2p-px-2 p2p-py-0.5 p2p-rounded p2p-bg-rose-50 p2p-text-rose-700 p2p-border p2p-border-rose-200";
                        }
                    }

                    if (lblTrustScore) {
                        lblTrustScore.textContent = `${data.trustScore} / 100`;
                        lblTrustScore.className = data.trustScore >= 80 ? "lbl-trust-score p2p-font-bold p2p-text-xs p2p-text-emerald-600" :
                            data.trustScore >= 40 ? "lbl-trust-score p2p-font-bold p2p-text-xs p2p-text-amber-500" :
                                "lbl-trust-score p2p-font-bold p2p-text-xs p2p-text-rose-600";
                    }

                    if (lblLastSeen) {
                        lblLastSeen.textContent = data.lastSeen ? new Date(data.lastSeen).toLocaleString() : "N/A";
                    }

                    if (lblSignatureTime) {
                        lblSignatureTime.textContent = data.signatureTime ? new Date(data.signatureTime).toLocaleString() : "N/A";
                    }

                    // Render Roles using template cloning (NO RAW HTML)
                    if (containerRoles) {
                        containerRoles.textContent = '';
                        if (data.roles && data.roles.length > 0) {
                            const tmplRole = document.getElementById('tmpl-role-badge');
                            data.roles.forEach(role => {
                                if (tmplRole) {
                                    const clone = tmplRole.content.cloneNode(true);
                                    const badge = clone.querySelector('.role-badge-text');
                                    if (badge) badge.textContent = role;
                                    containerRoles.appendChild(clone);
                                }
                            });
                        } else {
                            const tmplEmpty = document.getElementById('tmpl-empty-info');
                            if (tmplEmpty) {
                                const clone = tmplEmpty.content.cloneNode(true);
                                const msg = clone.querySelector('.msg-empty');
                                if (msg) msg.textContent = 'None';
                                containerRoles.appendChild(clone);
                            }
                        }
                    }

                    // Render Signaling Endpoints using template cloning (NO RAW HTML)
                    if (containerEndpoints) {
                        containerEndpoints.textContent = '';
                        if (data.signalingEndpoints && data.signalingEndpoints.length > 0) {
                            const tmplEndpoint = document.getElementById('tmpl-endpoint-item');
                            data.signalingEndpoints.forEach(ep => {
                                if (tmplEndpoint) {
                                    const clone = tmplEndpoint.content.cloneNode(true);
                                    const item = clone.querySelector('.endpoint-item-text');
                                    if (item) item.textContent = ep;
                                    containerEndpoints.appendChild(clone);
                                }
                            });
                        } else {
                            const tmplEmpty = document.getElementById('tmpl-empty-info');
                            if (tmplEmpty) {
                                const clone = tmplEmpty.content.cloneNode(true);
                                const msg = clone.querySelector('.msg-empty');
                                if (msg) msg.textContent = 'No signaling endpoints registered.';
                                containerEndpoints.appendChild(clone);
                            }
                        }
                    }

                    if (drawerLoading) drawerLoading.classList.add('p2p-hidden');
                    if (drawerContent) drawerContent.classList.remove('p2p-hidden');
                })
                .catch(err => {
                    console.error(`[${SCRIPT_ID}] Error fetching node details:`, err);
                    if (drawerLoading) {
                        drawerLoading.textContent = 'Failed to load historical node details.';
                        drawerLoading.className = 'drawer-loading p2p-py-4 p2p-text-xs p2p-text-rose-600 p2p-text-center';
                    }
                });
        },

        executePostAction: function (actionName, payload, verificationToken, confirmMsg = null, callback = null) {
            if (confirmMsg && !confirm(confirmMsg)) return;

            const url = `${window.location.origin}/${CONTROLLER_PATH}/${actionName}`;
            const params = new URLSearchParams();

            for (const key in payload) params.append(key, payload[key]);
            if (verificationToken) params.append('__RequestVerificationToken', verificationToken);

            fetch(url, {
                method: 'POST',
                body: params,
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'X-Requested-With': 'XMLHttpRequest'
                }
            })
                .then(res => res.json())
                .then(data => {
                    alert(data.message);
                    if (callback) callback();
                })
                .catch(err => console.error(`[${SCRIPT_ID}] Command error:`, err));
        },

        dispose: function () {
            if (this.pollTimer) clearInterval(this.pollTimer);
        }
    };

    const hook = document.querySelector(`[data-script-id="${SCRIPT_ID}"]`);
    if (hook) {
        window.ModuleRegistry[SCRIPT_ID].init(hook);
    }
})();