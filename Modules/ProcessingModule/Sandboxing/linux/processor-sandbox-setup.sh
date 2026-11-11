#!/usr/bin/env bash
# One-time root setup for the Processing module's OS-user sandbox on Linux.
#
# What it does (idempotent, safe to run again after moving folders):
#   - creates the sandbox group and the worker user (system user, no home, no login shell);
#   - adds the app user to the sandbox group (the app reads/cleans files the worker creates);
#   - installs /etc/sudoers.d/processor-sandbox-<worker>: the app user may run commands AS THE WORKER, nothing else
#     ("<app> ALL=(<worker>) NOPASSWD: ALL"). The worker has fewer rights than the app, so this grants the app nothing new;
#   - scripts + executor folders: readable by the group, not writable; new script versions inherit the group;
#   - results folder: owned by the app, group may only traverse it (no listing of job ids); job folders are opened to
#     the worker per step by the app itself and closed again afterwards (no root needed for that);
#   - --protect <dir> (the node home): removes "others" access below it, so the worker can't open the node's databases,
#     keys or settings through a path it can traverse (those files are created world-readable with the usual umask 022);
#   - parent folders the worker can't traverse (e.g. /home/<app> with mode 750) get traverse-only access for the worker
#     (ACL entry if setfacl exists, otherwise o+x);
#   - blocks ALL network access for the worker, including loopback (unless --allow-network), and installs a systemd unit
#     that re-applies the rule at boot.
#
# Usage (run as root):
#   processor-sandbox-setup.sh --app-user <user> --worker <name> --group <name> \
#       --scripts <dir> --results <dir> --executor-dir <dir> [--protect <node home>] [--allow-network]
#   processor-sandbox-setup.sh --uninstall --app-user <user> --worker <name> --group <name>
#
# After the first run, restart the app: a new group membership only applies to processes started afterwards.
set -euo pipefail

APP_USER=""; WORKER=""; GROUP=""; SCRIPTS=""; RESULTS=""; EXECUTOR_DIR=""; PROTECT=()
ALLOW_NETWORK=0; UNINSTALL=0

die()  { echo "[ERROR] $*" >&2; exit 1; }
info() { echo "[INFO] $*"; }
ok()   { echo "[OK] $*"; }
warn() { echo "[WARN] $*"; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --app-user)      APP_USER="$2"; shift 2 ;;
        --worker)        WORKER="$2"; shift 2 ;;
        --group)         GROUP="$2"; shift 2 ;;
        --scripts)       SCRIPTS="$2"; shift 2 ;;
        --results)       RESULTS="$2"; shift 2 ;;
        --executor-dir)  EXECUTOR_DIR="$2"; shift 2 ;;
        --protect)       PROTECT+=("$2"); shift 2 ;;
        --allow-network) ALLOW_NETWORK=1; shift ;;
        --uninstall)     UNINSTALL=1; shift ;;
        -h|--help)       sed -n '2,25p' "$0"; exit 0 ;;
        *) die "Unknown argument: $1" ;;
    esac
done

[[ $EUID -eq 0 ]] || die "Run as root (sudo $0 ...)."
NAME_RE='^[a-z_][a-z0-9_-]{0,31}$'
[[ -n "$APP_USER" ]] || die "--app-user is required."
[[ "$WORKER" =~ $NAME_RE ]] || die "Worker name '$WORKER' is not a valid Linux user name (lower case, max 32: ^[a-z_][a-z0-9_-]*)."
[[ "$GROUP" =~ $NAME_RE ]] || die "Group name '$GROUP' is not a valid Linux group name."
id -u "$APP_USER" >/dev/null 2>&1 || die "App user '$APP_USER' does not exist."
[[ "$WORKER" != "$APP_USER" ]] || die "The worker must be a different user than the app user."
[[ "$WORKER" != "root" ]] || die "The worker can't be root."

SUDOERS_FILE="/etc/sudoers.d/processor-sandbox-${WORKER}"
UNIT_NAME="processor-sandbox-net-${WORKER}.service"
UNIT_FILE="/etc/systemd/system/${UNIT_NAME}"
HAVE_SYSTEMD=0; [[ -d /run/systemd/system ]] && command -v systemctl >/dev/null 2>&1 && HAVE_SYSTEMD=1

net_rule() { # $1 = -C|-I|-D ; reject everything the worker sends, loopback included
    local rc=0
    if command -v iptables >/dev/null 2>&1; then
        iptables "$1" OUTPUT -m owner --uid-owner "$WORKER" -j REJECT 2>/dev/null || rc=1
        if command -v ip6tables >/dev/null 2>&1; then
            ip6tables "$1" OUTPUT -m owner --uid-owner "$WORKER" -j REJECT 2>/dev/null || rc=1
        fi
    else
        rc=1
    fi
    return $rc
}

remove_network_block() {
    while net_rule -C; do net_rule -D || break; done
    if [[ -f "$UNIT_FILE" ]]; then
        if [[ $HAVE_SYSTEMD -eq 1 ]]; then systemctl disable --now "$UNIT_NAME" >/dev/null 2>&1 || true; fi
        rm -f "$UNIT_FILE"
        [[ $HAVE_SYSTEMD -eq 1 ]] && systemctl daemon-reload || true
    fi
}

# ---------------------------------------------------------------- uninstall
if [[ $UNINSTALL -eq 1 ]]; then
    if id -u "$WORKER" >/dev/null 2>&1; then
        pkill -KILL -u "$WORKER" 2>/dev/null || true
        id -u "$WORKER" >/dev/null 2>&1 && remove_network_block || true
        userdel "$WORKER" && ok "Worker user '$WORKER' removed."
    fi
    rm -f "$SUDOERS_FILE" && ok "Sudo rule removed."
    gpasswd -d "$APP_USER" "$GROUP" >/dev/null 2>&1 || true
    getent group "$GROUP" >/dev/null && groupdel "$GROUP" 2>/dev/null && ok "Group '$GROUP' removed." || true
    info "Folder permissions were left as they are."
    exit 0
fi

[[ -n "$SCRIPTS" && -n "$RESULTS" && -n "$EXECUTOR_DIR" ]] || die "--scripts, --results and --executor-dir are required."
for p in "$SCRIPTS" "$RESULTS" "$EXECUTOR_DIR"; do [[ "$p" == /* ]] || die "Paths must be absolute: $p"; done
for p in "$SCRIPTS" "$RESULTS" "$EXECUTOR_DIR"; do [[ "$p" != "/" ]] || die "Refusing to change permissions of /."; done
APP_GROUP="$(id -gn "$APP_USER")"

# ---------------------------------------------------------------- 1. group + worker
if getent group "$GROUP" >/dev/null; then ok "Group '$GROUP' exists."
else groupadd --system "$GROUP" && ok "Group '$GROUP' created."; fi

NOLOGIN="$(command -v nologin || echo /usr/sbin/nologin)"
if id -u "$WORKER" >/dev/null 2>&1; then
    usermod --gid "$GROUP" --shell "$NOLOGIN" "$WORKER"
    ok "Worker '$WORKER' exists (primary group '$GROUP', no login)."
else
    useradd --system --gid "$GROUP" --no-create-home --home-dir /nonexistent --shell "$NOLOGIN" \
            --comment "Processor sandbox worker" "$WORKER"
    ok "Worker '$WORKER' created (system user, no home, no login)."
fi
passwd -l "$WORKER" >/dev/null 2>&1 || true

if id -nG "$APP_USER" | tr ' ' '\n' | grep -qx "$GROUP"; then ok "App user '$APP_USER' is in '$GROUP'."
else usermod -aG "$GROUP" "$APP_USER" && ok "App user '$APP_USER' added to '$GROUP' (restart the app to pick this up)."; fi

# ---------------------------------------------------------------- 2. sudo rule
command -v sudo >/dev/null 2>&1 || die "sudo is not installed (apt install sudo / dnf install sudo)."
TMP_SUDOERS="$(mktemp)"
cat > "$TMP_SUDOERS" <<EOF
# Processor sandbox: the app ($APP_USER) may run commands as the worker ($WORKER) only. Managed by processor-sandbox-setup.sh.
Defaults:$APP_USER !requiretty
$APP_USER ALL=($WORKER) NOPASSWD: ALL
EOF
visudo -cf "$TMP_SUDOERS" >/dev/null || { rm -f "$TMP_SUDOERS"; die "Generated sudoers rule failed validation."; }
install -o root -g root -m 0440 "$TMP_SUDOERS" "$SUDOERS_FILE"; rm -f "$TMP_SUDOERS"
ok "Sudo rule installed: $SUDOERS_FILE"

# ---------------------------------------------------------------- 3. folders
mkdir -p "$SCRIPTS" "$RESULTS"
[[ -d "$EXECUTOR_DIR" ]] || warn "Executor folder $EXECUTOR_DIR does not exist yet; run this again after copying the executor."

# scripts: group may read, nobody but the owner writes; new folders keep the group (setgid)
chgrp -R "$GROUP" "$SCRIPTS"
chmod -R g+rX,g-w,o-rwx "$SCRIPTS"
find "$SCRIPTS" -type d -exec chmod g+s {} +
ok "Scripts: $SCRIPTS (group '$GROUP' read-only)."

if [[ -d "$EXECUTOR_DIR" ]]; then
    chgrp -R "$GROUP" "$EXECUTOR_DIR"
    chmod -R g+rX,g-w "$EXECUTOR_DIR"
    ok "Executor: $EXECUTOR_DIR (group '$GROUP' read + execute)."
fi

# results: app owns it, the group may only pass through (x) - it can't list job ids.
# Job folders are opened per step by the app and closed again; existing ones are closed now.
chown "$APP_USER":"$GROUP" "$RESULTS"
chmod 0710 "$RESULTS"
find "$RESULTS" -mindepth 1 -maxdepth 1 -type d -user "$APP_USER" -exec chgrp "$APP_GROUP" {} + -exec chmod 0700 {} +
ok "Results: $RESULTS (owner '$APP_USER', group '$GROUP' traverse only, job folders closed)."

# ---------------------------------------------------------------- 4a. protect the node's own files from "others"
for p in "${PROTECT[@]+"${PROTECT[@]}"}"; do
    [[ "$p" == /* ]] || die "--protect needs an absolute path: $p"
    # never a system folder: at least two levels deep (/srv/node1, /home/app/node), and owned by the app user
    [[ "$(echo "$p" | tr -s '/' | awk -F/ '{print NF-1}')" -ge 2 ]] || die "Refusing to --protect a top-level folder: $p"
    [[ -d "$p" ]] || { warn "--protect folder $p does not exist; skipped."; continue; }
    [[ "$(stat -c %U "$p")" == "$APP_USER" ]] || die "--protect folder $p is not owned by $APP_USER."
    chmod -R o-rwx "$p"
    ok "Protected $p: no access for other users (databases, keys, settings)."
done

# ---------------------------------------------------------------- 4b. traverse access to parent folders
can_traverse() { sudo -n -u "$WORKER" test -x "$1" 2>/dev/null; }
grant_parents() {
    local d; d="$(dirname "$1")"
    while [[ "$d" != "/" ]]; do
        if ! can_traverse "$d"; then
            if command -v setfacl >/dev/null 2>&1; then
                setfacl -m "u:$WORKER:--x" "$d" && info "Traverse-only ACL for '$WORKER' on $d"
            else
                chmod o+x "$d" && warn "No setfacl (package 'acl'): gave everyone traverse-only (o+x) on $d"
            fi
        fi
        d="$(dirname "$d")"
    done
}
for p in "$SCRIPTS" "$RESULTS" "$EXECUTOR_DIR"; do [[ -e "$p" ]] && grant_parents "$p"; done
for p in "$SCRIPTS" "$RESULTS"; do
    can_traverse "$p" && ok "Worker can reach $p" || warn "Worker still can't reach $p"
done

# ---------------------------------------------------------------- 5. network
if [[ $ALLOW_NETWORK -eq 1 ]]; then
    remove_network_block
    warn "Network access for '$WORKER' is ALLOWED (--allow-network)."
else
    if net_rule -C; then ok "Network block for '$WORKER' already active."
    elif net_rule -I; then ok "Network blocked for '$WORKER' (all outgoing traffic, loopback included)."
    else warn "Could not add the iptables rule (no iptables, or no permission in this container). Scripts CAN use the network."; fi

    if [[ $HAVE_SYSTEMD -eq 1 ]]; then
        IPT="$(command -v iptables || true)"; IP6T="$(command -v ip6tables || true)"
        if [[ -n "$IPT" ]]; then
            {
                echo "[Unit]"
                echo "Description=Processor sandbox: block network for $WORKER"
                echo "After=network-pre.target"
                echo "[Service]"
                echo "Type=oneshot"
                echo "RemainAfterExit=yes"
                echo "ExecStart=/bin/sh -c '$IPT -C OUTPUT -m owner --uid-owner $WORKER -j REJECT 2>/dev/null || $IPT -I OUTPUT -m owner --uid-owner $WORKER -j REJECT'"
                [[ -n "$IP6T" ]] && echo "ExecStart=/bin/sh -c '$IP6T -C OUTPUT -m owner --uid-owner $WORKER -j REJECT 2>/dev/null || $IP6T -I OUTPUT -m owner --uid-owner $WORKER -j REJECT'"
                echo "[Install]"
                echo "WantedBy=multi-user.target"
            } > "$UNIT_FILE"
            systemctl daemon-reload && systemctl enable "$UNIT_NAME" >/dev/null 2>&1 \
                && ok "Network block re-applied at boot ($UNIT_NAME)." \
                || warn "Could not enable $UNIT_NAME; the block lasts until reboot."
        fi
    else
        warn "No systemd: the network block lasts until reboot (save it with your firewall tool, e.g. netfilter-persistent save)."
    fi
fi

echo
ok "Sandbox setup finished for worker '$WORKER'."
info "Restart the app if '$APP_USER' was just added to '$GROUP'."
