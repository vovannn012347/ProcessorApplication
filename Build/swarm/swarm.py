#!/usr/bin/env python3
"""
Swarm test nodes: prepare their node homes, start, stop - from ONE NodeTrackerSwarm build.

Layout (platform = --rid, default win-x64):
    bin/<rid>/TrackerSwarm/ProcessorApplication/   program files + shared appsettings.*.NodeTrackerSwarm.json
    bin/<rid>/TrackerSwarm/nodes/<name>/           one node: its own database, keys, Module_Data, settings override

Every node starts FRESH by default: no database is copied, so each node creates its own root key (node.key), its
own server key chain, its own P2P identity and its own Worker key on first start. The Admin user is created on that
first start from <home>/admin-seed.json (the app deletes the file once the user exists).

Per node the script writes:
    Modules/P2PModule/appsettings.P2P.NodeTrackerSwarm.json
        EnforcedNodeIdentifier = the node's reserved name (taken when its identity is created)
        Endpoints              = localhost:<its port>
        SeedEndpoints          = previous node in a ring (keeps the graph connected) + random others (+ --extra-seeds)
    Modules/ProviderlessModule/appsettings.Providerless.NodeTrackerSwarm.json   (only with --clinic-ids)
    admin-seed.json                                                             (only while the node has no database)
    swarm-node.json                                                             (name, port, url, seeds; used by "start")
plus the preset files <project>/configFiles/TrackerSwarm/** (only files the home doesn't have yet).

Commands (python swarm.py <command> --help for the options):
    build     dotnet build the NodeTrackerSwarm configuration
    prepare   create / update the node homes (keeps existing data)
    fresh     wipe every node's data (databases, keys, Module_Data), then prepare again - a clean start
    start     start the nodes (http only, one console window per node, titled with the node name)
    stop      stop the nodes started by "start"
    status    list the homes and which nodes are running
    up        prepare + start
    consensus sign in to one node as Admin and press "Trigger consensus" (the others join through start pings)
    test      the whole convergence test in one go: stop, (build), fresh homes, start, wait until the nodes are
              connected, trigger the consensus, watch convergence, report  (swarm-test.bat)
    check     one convergence snapshot of every node (read straight from the node homes), appended to
              <group>/convergence.log; --watch N repeats every N seconds, --until-converged stops when done
    analyze   report from convergence.log + every node's Module_Data/P2P/consensus_events.jsonl,
              and CSV files for charts (convergence.csv, consensus_events.csv)
    viz       consensus_viz.html: the run replayed step by step (merges, deltas, start pings) as a growing graph;
              open it in a browser. Other runs can be loaded into it (drop the consensus_events.jsonl files)
    archive   copy the last run's logs out of the node homes into <group>/runs/<time>_N<count>.../ (test and
              bench do this after every run; --archive-dir elsewhere, --no-archive to skip)
    bench     the whole test for several node counts, a few times each; a row per run in runs.csv

Convergence test:
    swarm.bat fresh --count 20 --yes  &&  swarm.bat start
    (wait until the nodes are connected, a few minutes)
    swarm.bat consensus
    swarm.bat check --watch 15 --until-converged
    swarm.bat analyze
Only the Python standard library is used.
"""
import argparse
import csv
import getpass
import http.cookiejar
import json
import os
import random
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENVIRONMENT = "NodeTrackerSwarm"
NAME_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,62}$")
SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}


# ---------------------------------------------------------------- paths

def group_root(rid):
    return ROOT / "bin" / rid / "TrackerSwarm"


def app_folder(rid):
    return group_root(rid) / "ProcessorApplication"


def homes_root(rid):
    return group_root(rid) / "nodes"


def exe_path(rid):
    name = "ProcessorApplication.exe" if rid.startswith("win") else "ProcessorApplication"
    return app_folder(rid) / name


def pid_file(rid):
    return group_root(rid) / "swarm.pids.json"


def write_json(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=2, ensure_ascii=False), encoding="utf-8")


def read_json(path, default=None):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return default


def node_homes(rid):
    """Prepared homes (those with swarm-node.json), by port."""
    result = []
    root = homes_root(rid)
    if not root.is_dir():
        return result
    for d in sorted(root.iterdir()):
        info = read_json(d / "swarm-node.json") if d.is_dir() else None
        if info and d.name != "_template":
            result.append((d, info))
    return sorted(result, key=lambda x: int(x[1].get("Port", 0)))


# ---------------------------------------------------------------- prepare

def strip_json_comments(text):
    """appsettings files may contain // and /* */ comments; drop them outside strings."""
    out, i, n, in_str = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1]); i += 2; continue
            if c == '"':
                in_str = False
            i += 1; continue
        if c == '"':
            in_str = True; out.append(c); i += 1; continue
        if text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        if text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        out.append(c); i += 1
    return "".join(out)


WORKER_SETTINGS_CANDIDATES = [
    "Modules/ProviderlessModule/appsettings.Providerless.NodeTracker1Debug.json",
    "Modules/ProviderlessModule/appsettings.Providerless.NodeTracker2Debug.json",
    "Modules/ProviderlessModule/appsettings.Providerless.NodeClient1Debug.json",
]
CLOUDFLARED_CANDIDATES = [
    "Modules/ProviderlessModule/configFiles/TrackerSwarm/Module_Data/Portal/bin",
    "Modules/ProviderlessModule/configFiles/Tracker1/Module_Data/Portal/bin",
    "Modules/ProviderlessModule/configFiles/Debug/Module_Data/Portal/bin",
]


def load_worker_settings(path_arg):
    """ServiceUrl + JoinKey of the Cloudflare Worker tunnel, from --worker-settings or the test nodes' settings files."""
    paths = [Path(path_arg)] if path_arg else [ROOT / c for c in WORKER_SETTINGS_CANDIDATES]
    for p in paths:
        try:
            data = json.loads(strip_json_comments(p.read_text(encoding="utf-8-sig")))
        except (OSError, ValueError):
            continue
        w = data.get("CloudflareWorkerSettings") or {}
        if w.get("ServiceUrl") and w.get("JoinKey"):
            return {"ServiceUrl": w["ServiceUrl"], "JoinKey": w["JoinKey"]}, p
    return None, None


def copy_cloudflared(home, rid):
    """One cloudflared copy per public node home (saves each node a ~55 MB download)."""
    name = "cloudflared.exe" if rid.startswith("win") else "cloudflared"
    dest = home / "Module_Data" / "Portal" / "bin" / name
    if dest.exists():
        return
    for c in CLOUDFLARED_CANDIDATES:
        src = ROOT / c / name
        if src.exists():
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dest)
            return

def preset_dirs():
    found = []
    for dirpath, dirnames, _ in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        if os.path.basename(dirpath) == "configFiles":
            p = Path(dirpath) / "TrackerSwarm"
            if p.is_dir():
                found.append(p)
            dirnames[:] = []
    return found


def copy_presets(presets, home):
    """Only files the home doesn't have; a -wal/-shm only together with a missing database."""
    for preset in presets:
        for src in preset.rglob("*"):
            if not src.is_file():
                continue
            dest = home / src.relative_to(preset)
            if dest.exists():
                continue
            db = Path(re.sub(r"-(wal|shm)$", "", str(dest)))
            if db != dest and db.exists():
                continue
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dest)


def resolve_names(args):
    if args.names_file:
        lines = Path(args.names_file).read_text(encoding="utf-8-sig").splitlines()
        names = [re.sub(r"#.*$", "", l).strip() for l in lines]
        names = [n for n in names if n]
    elif args.names:
        names = [n.strip() for n in args.names.split(",") if n.strip()]
    else:
        names = [f"{args.prefix}-{i:03d}" for i in range(1, args.count + 1)]
    names = [n.lower() for n in names]
    bad = [n for n in names if not NAME_RE.match(n) or n == "_template"]
    if bad:
        sys.exit(f"Bad node name(s): {', '.join(bad)} (a-z, 0-9, '-', start with a letter/digit, max 63)")
    dupes = sorted({n for n in names if names.count(n) > 1})
    if dupes:
        sys.exit(f"Duplicate node names: {', '.join(dupes)}")
    if len(names) < 2:
        sys.exit("A swarm needs at least 2 nodes.")
    return names


def admin_password(args):
    if args.admin_password:
        return args.admin_password
    env = os.environ.get("SWARM_ADMIN_PASSWORD")
    if env:
        return env
    while True:
        p1 = getpass.getpass("Admin password for every swarm node (input hidden): ")
        p2 = getpass.getpass("Again: ")
        if p1 and p1 == p2:
            return p1
        print("Empty or not the same - again.")


def prepare(args):
    names = resolve_names(args)
    count = len(names)
    seeds_per_node = max(1, min(args.seeds_per_node, count - 1))
    rng = random.Random(args.random_seed) if args.random_seed else random.Random()
    extra = [s.strip() for s in (args.extra_seeds or "").split(",") if s.strip()]

    nodes = []
    for i, name in enumerate(names):
        port = args.first_port + i
        nodes.append({"Name": name, "Port": port, "Url": f"http://localhost:{port}"})
    for i, node in enumerate(nodes):
        picked = [(i - 1) % count]                      # ring predecessor: the mesh is always connected
        while len(picked) < seeds_per_node:
            j = rng.randrange(count)
            if j != i and j not in picked:
                picked.append(j)
        node["Seeds"] = [nodes[j]["Url"] for j in picked] + extra

    # Swarm test of attacks: N nodes misbehave on purpose (never the first node: it triggers the consensus)
    attacks = [a.strip() for a in (getattr(args, "attacks", "") or "").split(",") if a.strip()]
    evil_count = max(0, min(getattr(args, "evil", 0) or 0, count - 1))
    evil_names = set(rng.sample(names[1:], evil_count)) if evil_count and attacks else set()
    if evil_count and not attacks:
        print("--evil given without --attacks: no node misbehaves.")

    public_count = max(0, min(getattr(args, "public", 0) or 0, count))
    worker = None
    if public_count:
        worker, source = load_worker_settings(getattr(args, "worker_settings", ""))
        if not worker:
            sys.exit("--public needs the Cloudflare Worker settings (ServiceUrl + JoinKey): pass --worker-settings <json file> "
                     "(a Providerless settings file with a CloudflareWorkerSettings section), or fill them in "
                     + WORKER_SETTINGS_CANDIDATES[0])
        print(f"Public tunnels for the first {public_count} node(s); Worker settings from {source}")

    presets = preset_dirs()
    password = None
    root = homes_root(args.rid)
    created = updated = seeded = 0

    for node in nodes:
        home = root / node["Name"]
        is_new = not home.exists()
        home.mkdir(parents=True, exist_ok=True)
        created += is_new
        updated += not is_new
        copy_presets(presets, home)

        p2p_override = {
            "P2PSettings": {
                "EnforcedNodeIdentifier": node["Name"],
                # advertised endpoints are kept without a scheme (https is tried first, then http)
                "Endpoints": [f"localhost:{node['Port']}"],
                "SeedEndpoints": node["Seeds"],
            }
        }
        node["Evil"] = attacks if node["Name"] in evil_names else []
        if node["Evil"]:
            p2p_override["EvilSettings"] = {"Attacks": attacks, "Rate": getattr(args, "evil_rate", 1.0)}
        write_json(home / "Modules" / "P2PModule" / f"appsettings.P2P.{ENVIRONMENT}.json", p2p_override)
        clinic = home / "Modules" / "ProviderlessModule" / f"appsettings.Providerless.{ENVIRONMENT}.json"
        node["Public"] = nodes.index(node) < public_count
        if node["Public"]:
            # its own Cloudflare Worker tunnel: subdomain <name>.<zone>, advertised to the mesh next to localhost
            write_json(clinic, {
                "PortalAccessSettings": {"Enabled": True, "RegistryEnabled": False,
                                         "SelectedTunnel": "cloudflareworker", "EnforcedPCIdentifier": node["Name"]},
                "CloudflareWorkerSettings": worker,
            })
            copy_cloudflared(home, args.rid)
        elif args.clinic_ids:
            write_json(clinic, {"PortalAccessSettings": {"EnforcedPCIdentifier": node["Name"]}})
        elif clinic.exists():
            clinic.unlink()

        # A fresh node (no database yet) creates its Admin from this file on first start, then deletes it.
        if not (home / "medicalsystem.db").exists():
            if password is None:
                password = admin_password(args)
            write_json(home / "admin-seed.json", {
                "Name": args.admin_name, "Surname": args.admin_surname,
                "Email": args.admin_email, "Password": password,
            })
            seeded += 1

        write_json(home / "swarm-node.json", node)

    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    append_line(convergence_log(args.rid), {"t": stamp, "kind": "evil_nodes", "nodes": sorted(evil_names), "attacks": attacks if evil_names else []})
    if evil_names:
        print(f"Evil nodes ({', '.join(attacks)}): {', '.join(sorted(evil_names))}")

    # Homes of an earlier, bigger swarm stay on disk, but without swarm-node.json "start" no longer sees them
    # (a 5-node run used to start all 10).
    keep = {n["Name"] for n in nodes}
    retired = 0
    if root.is_dir():
        for d in root.iterdir():
            if d.is_dir() and d.name not in keep and (d / "swarm-node.json").exists():
                (d / "swarm-node.json").unlink()
                retired += 1
    if retired:
        print(f"{retired} home(s) of nodes not in this swarm set aside (their swarm-node.json removed).")

    write_json(group_root(args.rid) / "swarm-homes.json",
               [{"Name": n["Name"], "Port": n["Port"], "Url": n["Url"]} for n in nodes])

    print(f"Swarm homes in {root}: {created} new, {updated} updated; {seeded} will create their Admin on first start.")
    print(f"Nodes {names[0]} .. {names[-1]}, ports {args.first_port}..{args.first_port + count - 1}, "
          f"{seeds_per_node} seed(s) each" + (f" + {', '.join(extra)}" if extra else "") + ".")
    if presets:
        print("Presets from: " + ", ".join(str(p.relative_to(ROOT)) for p in presets))


# ---------------------------------------------------------------- fresh

def wipe_node_data(home):
    """Everything the node created itself; its settings override, swarm-node.json and presets stay.
    Module_Data/Portal stays too: its provisioning key owns the node's Cloudflare subdomain (a new key couldn't
    claim the same name again for 30 days) and it holds the cloudflared binary."""
    for p in home.rglob("*"):
        if p.is_file() and re.search(r"\.db(-wal|-shm)?$", p.name):
            p.unlink()
    module_data = home / "Module_Data"
    if module_data.is_dir():
        for child in module_data.iterdir():
            if child.name == "Portal":
                continue
            if child.is_dir():
                shutil.rmtree(child, ignore_errors=True)
            else:
                child.unlink(missing_ok=True)
    for d in ("data", ".build", "logs"):
        shutil.rmtree(home / d, ignore_errors=True)
    for f in ("admin-seed.json",):
        (home / f).unlink(missing_ok=True)


def fresh(args):
    running = alive_pids(args.rid)
    if running:
        sys.exit(f"{len(running)} swarm node(s) are still running - stop them first (swarm.bat stop).")
    homes = node_homes(args.rid)
    if homes and not args.yes:
        answer = input(f"Delete databases, keys and Module_Data of {len(homes)} swarm node(s)? Type YES: ")
        if answer != "YES":
            sys.exit("Cancelled.")
    for home, _ in homes:
        wipe_node_data(home)
    print(f"Wiped {len(homes)} node home(s).")
    prepare(args)


# ---------------------------------------------------------------- start / stop

def port_busy(port):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(0.2)
        return s.connect_ex(("127.0.0.1", port)) == 0


def pid_alive(pid):
    if os.name == "nt":
        out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
                             capture_output=True, text=True).stdout
        return "ProcessorApplication" in out
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def running_app_pids():
    """PIDs of all running ProcessorApplication processes, from ONE process listing. (Asking tasklist once per
    node took ~1-3 s each on a loaded PC: a 20-node snapshot sat silent for a minute.)"""
    if os.name != "nt":
        return None
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq ProcessorApplication.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    pids = set()
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[1].strip('"').isdigit():
            pids.add(int(parts[1].strip('"')))
    return pids


def alive_pids(rid):
    entries = read_json(pid_file(rid), []) or []
    running = running_app_pids()
    if running is None:
        return [e for e in entries if pid_alive(int(e["Pid"]))]
    return [e for e in entries if int(e["Pid"]) in running]


def start(args):
    exe = exe_path(args.rid)
    if not exe.exists():
        sys.exit(f"Not built: {exe}. Run: swarm.bat build")
    homes = node_homes(args.rid)
    if not homes:
        sys.exit("No swarm homes yet. Run: swarm.bat prepare --count 50")
    if args.only:
        homes = homes[:args.only]

    busy = [(h.name, i["Port"]) for h, i in homes if port_busy(int(i["Port"]))]
    if busy:
        sys.exit("Ports already in use: " + ", ".join(f"{n}:{p}" for n, p in busy) + " (swarm.bat stop?)")

    started = alive_pids(args.rid)
    for k, (home, info) in enumerate(homes):
        cmd = [str(exe), "--node-home", str(home), "--environment", ENVIRONMENT,
               "--urls", f"http://localhost:{info['Port']}", f"--SettingsIngest:Mode={args.reingest}"]
        kwargs = {"cwd": str(home)}
        if os.name == "nt":
            if args.window == "hidden":
                kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
            else:
                kwargs["creationflags"] = subprocess.CREATE_NEW_CONSOLE
                si = subprocess.STARTUPINFO()
                si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                si.wShowWindow = 7 if args.window == "minimized" else 1   # SW_SHOWMINNOACTIVE / SW_SHOWNORMAL
                kwargs["startupinfo"] = si
        else:
            log = open(home / "console.log", "a", encoding="utf-8")
            kwargs.update(stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        proc = subprocess.Popen(cmd, **kwargs)
        started.append({"Node": info["Name"], "Pid": proc.pid, "Url": info["Url"]})
        print(f"{info['Name']:<14} PID {proc.pid:<7} {info['Url']}")
        time.sleep(5 if k == 0 else args.delay)       # the first node is everybody's ring neighbour: head start

    write_json(pid_file(args.rid), started)
    print(f"\nStarted {len(homes)} node(s), settings re-ingest: {args.reingest}. Stop: swarm.bat stop")


def stop(args):
    entries = read_json(pid_file(args.rid), []) or []
    n = 0
    for e in entries:
        pid = int(e["Pid"])
        if not pid_alive(pid):
            continue
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
        else:
            try:
                os.kill(pid, 15)
            except OSError:
                pass
        n += 1
    pid_file(args.rid).unlink(missing_ok=True)
    print(f"Stopped {n} swarm node(s).")


def status(args):
    running = {e["Node"]: e for e in alive_pids(args.rid)}
    homes = node_homes(args.rid)
    for home, info in homes:
        state = f"running, PID {running[info['Name']]['Pid']}" if info["Name"] in running else "stopped"
        has_db = "db" if (home / "medicalsystem.db").exists() else "fresh"
        public = "public" if info.get("Public") else ""
        print(f"{info['Name']:<14} {info['Url']:<24} {has_db:<6} {public:<7} {state}")
    print(f"{len(homes)} home(s), {len(running)} running.")


def build(args):
    sln = ROOT / "ProcessorApplication.sln"
    cmd = ["dotnet", "build", str(sln), "-c", ENVIRONMENT, f"-p:BuildRid={args.rid}", "--nologo", "-v", "minimal"]
    print(" ".join(cmd))
    sys.exit(subprocess.run(cmd).returncode)


# ---------------------------------------------------------------- consensus trigger

TOKEN_RE = re.compile(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"|value="([^"]+)"[^>]*name="__RequestVerificationToken"')


def _token(html):
    m = TOKEN_RE.search(html)
    return (m.group(1) or m.group(2)) if m else None


def consensus(args):
    homes = node_homes(args.rid)
    if not homes:
        sys.exit("No swarm homes yet.")
    if args.node:
        match = [(h, i) for h, i in homes if i["Name"] == args.node]
        if not match:
            sys.exit(f"No swarm node named {args.node}.")
        home, info = match[0]
    else:
        home, info = homes[0]
    base = info["Url"].rstrip("/")
    password = admin_password(args)

    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))

    def get(path):
        with opener.open(base + path, timeout=30) as r:
            return r.read().decode("utf-8", "replace"), r.geturl()

    def post(path, fields, ajax=False):
        req = urllib.request.Request(base + path, data=urllib.parse.urlencode(fields).encode())
        if ajax:
            req.add_header("X-Requested-With", "XMLHttpRequest")
        with opener.open(req, timeout=60) as r:
            return r.read().decode("utf-8", "replace"), r.geturl()

    try:
        html, _ = get("/Main/Account/Login")
        token = _token(html)
        if not token:
            sys.exit(f"{info['Name']}: no sign-in form at {base}/Main/Account/Login (node still starting?)")
        _, landed = post("/Main/Account/Login", {"UserName": args.admin_user, "Password": password,
                                                  "ReturnUrl": "", "__RequestVerificationToken": token})
        if "/Account/Login" in landed:
            sys.exit(f"{info['Name']}: sign-in as {args.admin_user} failed (password?).")

        html, landed = get("/P2P/HistoryMonitor/Index")
        token = _token(html)
        if not token or "/Account/" in landed:
            sys.exit(f"{info['Name']}: no access to the History Monitor page (NetworkAdmin role?).")
        body, _ = post("/P2P/HistoryMonitor/TriggerConsensus", {"__RequestVerificationToken": token}, ajax=True)
    except urllib.error.URLError as e:
        sys.exit(f"{info['Name']} ({base}) not reachable: {e}")

    result = json.loads(body) if body.strip().startswith("{") else {"success": False, "message": body[:200]}
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    append_line(convergence_log(args.rid), {"t": stamp, "kind": "trigger", "node": info["Name"],
                                            "success": bool(result.get("success")), "message": result.get("message")})
    print(f"{info['Name']}: {result.get('message')}")
    if not result.get("success"):
        sys.exit(1)


# ---------------------------------------------------------------- convergence check / analysis

def convergence_log(rid):
    return group_root(rid) / "convergence.log"


def append_line(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(obj, ensure_ascii=False) + "\n")


def read_lines(path):
    out = []
    try:
        with open(path, encoding="utf-8-sig") as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        out.append(json.loads(line))
                    except ValueError:
                        pass
    except OSError:
        pass
    return out


def last_line(path, chunk=16384):
    """The last JSON line of a file, reading only its tail (event logs grow to megabytes)."""
    try:
        with open(path, "rb") as f:
            f.seek(0, os.SEEK_END)
            size = f.tell()
            f.seek(max(0, size - chunk))
            tail = f.read().decode("utf-8", errors="ignore")
        for line in reversed(tail.splitlines()):
            line = line.strip().lstrip("\ufeff")
            if line:
                try:
                    return json.loads(line)
                except ValueError:
                    continue
    except OSError:
        pass
    return None


def p2p_dir(home):
    return home / "Module_Data" / "P2P"


def known_live(home):
    """Peers the node knows right now (its peer store, filled by gossip and consensus)."""
    d = p2p_dir(home) / "consensus_latest" / "currentIndex"
    return {f.stem for f in d.glob("*.json")} if d.is_dir() else set()


def known_consensus(home):
    """Peers in the node's newest consensus day (history/<yyyy-MM-dd>/peers) and that day."""
    hist = p2p_dir(home) / "history"
    if not hist.is_dir():
        return set(), None
    days = sorted(d for d in hist.iterdir() if d.is_dir() and re.match(r"\d{4}-\d{2}-\d{2}$", d.name))
    for day in reversed(days):
        peers = day / "peers"
        if peers.is_dir():
            return {f.stem for f in peers.glob("*.json")}, day.name
    return set(), None


def snapshot(rid):
    homes = node_homes(rid)
    names = [i["Name"] for _, i in homes]
    running = {e["Node"] for e in alive_pids(rid)}
    per_node = {}
    for home, info in homes:
        name = info["Name"]
        live = known_live(home)
        cons, day = known_consensus(home)
        last = last_line(p2p_dir(home) / "consensus_events.jsonl")
        others = set(names) - {name}
        per_node[name] = {
            "running": name in running,
            "live": len(live & others),            # other swarm nodes it knows (its own entry doesn't count)
            "consensus": len(cons & others),
            "consensus_day": day,
            "missing_live": sorted(others - live),
            "missing_consensus": sorted(others - cons),
            "foreign": len((live | cons) - set(names)),   # ids that aren't swarm names (old identities etc.)
            "last_event": f"{last['ev']}@{last['t']}" if last else None,
        }
    n = len(names)
    complete_live = sum(1 for v in per_node.values() if not v["missing_live"])
    complete_cons = sum(1 for v in per_node.values() if not v["missing_consensus"])
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    return {"t": stamp, "kind": "snapshot", "nodes": n, "running": len(running & set(names)),
            "complete_live": complete_live, "complete_consensus": complete_cons,
            "avg_live": round(sum(v["live"] for v in per_node.values()) / n, 2) if n else 0,
            "avg_consensus": round(sum(v["consensus"] for v in per_node.values()) / n, 2) if n else 0,
            "per_node": per_node}


def print_snapshot(s, verbose):
    n = s["nodes"]
    if verbose:
        print(f"{'node':<14} {'live':>5} {'cons':>5} {'day':<11} last event")
        for name, v in s["per_node"].items():
            flag = "" if v["running"] else "  (stopped)"
            print(f"{name:<14} {v['live']:>5} {v['consensus']:>5} {v['consensus_day'] or '-':<11} {v['last_event'] or '-'}{flag}")
    worst = sorted(s["per_node"].items(), key=lambda kv: len(kv[1]["missing_consensus"]), reverse=True)[:3]
    def lacks(v):
        miss = sorted(v["missing_consensus"])
        return f"{len(miss)} ({', '.join(miss)})" if len(miss) <= 4 else f"{len(miss)} ({', '.join(miss[:4])}, ...)"
    gaps = "; ".join(f"{k} lacks {lacks(v)}" for k, v in worst if v["missing_consensus"])
    print(f"[{s['t']}] {s['running']}/{n} running | live complete {s['complete_live']}/{n} (avg {s['avg_live']}/{n - 1}) | "
          f"consensus complete {s['complete_consensus']}/{n} (avg {s['avg_consensus']}/{n - 1})" + (f" | {gaps}" if gaps else ""))


def check(args):
    if not node_homes(args.rid):
        sys.exit("No swarm homes yet.")
    log = convergence_log(args.rid)
    while True:
        s = snapshot(args.rid)
        append_line(log, s)
        print_snapshot(s, verbose=args.verbose or not args.watch)
        done = s["complete_consensus"] == s["nodes"]
        if not args.watch or (args.until_converged and done):
            if args.until_converged and done:
                print("Converged: every node's consensus holds every other node.")
            break
        time.sleep(args.watch)
    print(f"Logged to {log}")


def _parse_t(t):
    return datetime.strptime(t.replace("Z", ""), "%Y-%m-%dT%H:%M:%S.%f").replace(tzinfo=timezone.utc)


RUNS_COLUMNS = ["run", "nodes", "evil", "attacks", "merges", "merges_per_node", "deltas", "pulls", "chain_depth",
                "t_conv_s", "t95_s", "t99_s", "bytes_total", "bytes_per_node", "rec_bytes", "omega", "consistency_pct", "caught", "false_positives"]


def run_metrics(events, names, evil_nodes):
    """Numbers for one run, from the nodes' consensus events (since the trigger)."""
    n = len(names)
    starts = [e for e in events if e["ev"] == "start"]
    if not starts:
        return None
    t0 = _parse_t(starts[0]["t"])
    start_bytes = {}
    for e in starts:
        start_bytes.setdefault(e["_node"], e.get("bytes", 0) or 0)
    # when each node first held all the others, and its traffic by then
    done_at, bytes_at = {}, {}
    for e in events:
        if e["ev"] == "progress" and e["_node"] not in done_at and (e.get("held") or 0) >= n - 1:
            done_at[e["_node"]] = _parse_t(e["t"])
            bytes_at[e["_node"]] = (e.get("bytes") or 0) - start_bytes.get(e["_node"], 0)
    for e in events:  # nodes that converged without a progress line after it: their finish traffic
        if e["ev"] == "finish" and e["_node"] not in bytes_at:
            bytes_at[e["_node"]] = (e.get("bytes") or 0) - start_bytes.get(e["_node"], 0)
    swaps, last = 0, {}
    for e in events:
        if e["ev"] in ("merge", "merge_in") and e.get("ok", True) and e.get("peer"):
            k = tuple(sorted((e["_node"], e["peer"])))
            t = _parse_t(e["t"])
            if k in last and (t - last[k]).total_seconds() < 3:
                continue
            last[k] = t
            swaps += 1
    depth = max([e.get("order") or 0 for e in events if e["ev"] == "merge_in"], default=0)
    rec = sorted(e.get("rec_bytes") for e in events if e["ev"] == "finish" and e.get("rec_bytes"))
    rec_bytes = rec[len(rec) // 2] if rec else None
    total = sum(max(0, b) for b in bytes_at.values())
    # coverage over time: share of all (node, other node) pairs held, from each node's latest progress line
    t_q = {}
    latest = {}
    for e in events:
        if e["ev"] == "progress" and e.get("held") is not None:
            latest[e["_node"]] = max(latest.get(e["_node"], 0), e["held"])
            cov = sum(latest.values()) / max(1, n * (n - 1))
            for q in (0.95, 0.99):
                if q not in t_q and cov >= q:
                    t_q[q] = (_parse_t(e["t"]) - t0).total_seconds()
    complete = sum(1 for x in names if x in done_at)
    t_conv = (max(done_at.values()) - t0).total_seconds() if complete == n else None
    found = [e for e in events if e["ev"] == "evidence"]
    return {
        "run": starts[0]["t"], "nodes": n, "evil": len(evil_nodes), "attacks": "",
        "merges": swaps, "merges_per_node": round(swaps / n, 2) if n else 0,
        "deltas": sum(1 for e in events if e["ev"] == "delta"),
        "pulls": sum(1 for e in events if e["ev"] == "pull"),
        "chain_depth": depth,
        "t_conv_s": round(t_conv, 2) if t_conv is not None else "",
        "t95_s": round(t_q[0.95], 2) if 0.95 in t_q else "",
        "t99_s": round(t_q[0.99], 2) if 0.99 in t_q else "",
        "bytes_total": total, "bytes_per_node": round(total / n) if n else 0,
        "rec_bytes": rec_bytes or "",
        "omega": round(total / (n * (n - 1) * rec_bytes), 2) if rec_bytes and n > 1 else "",
        "consistency_pct": round(100.0 * complete / n, 1) if n else 0,
        "caught": len({e.get("peer") for e in found if e.get("peer") in evil_nodes}),
        "false_positives": len({e.get("peer") for e in found if e.get("peer") not in evil_nodes}),
    }


def analyze(args):
    log = convergence_log(args.rid)
    entries = read_lines(log)
    homes = node_homes(args.rid)
    names = [i["Name"] for _, i in homes]
    out_dir = group_root(args.rid)

    # ---- per-node consensus events
    events = []
    for home, info in homes:
        for e in read_lines(p2p_dir(home) / "consensus_events.jsonl"):
            e["_node"] = info["Name"]
            events.append(e)
    triggers = [e for e in entries if e.get("kind") == "trigger" and e.get("success")]
    # the trigger line is written after the node answered: its own "start" can be a few ms earlier
    since = _parse_t(triggers[-1]["t"]) - timedelta(seconds=5) if triggers and not args.all else None
    if args.since:
        since = _parse_t(args.since if "." in args.since else args.since + ".000")
    if since:
        events = [e for e in events if _parse_t(e["t"]) >= since]
        entries = [e for e in entries if _parse_t(e["t"]) >= since]
    events.sort(key=lambda e: e["t"])

    print(f"Swarm: {len(names)} nodes" + (f", from {since.isoformat()} (last trigger; --all for everything)" if since else ""))
    if events:
        t0 = _parse_t(events[0]["t"])
        starts = {}
        finishes = {}
        merges = {}
        for e in events:
            n = e["_node"]
            if e["ev"] == "start":
                starts.setdefault(n, e)
            elif e["ev"] == "finish":
                finishes[n] = e
            elif e["ev"] == "merge":
                ok, total = merges.get(n, (0, 0))
                merges[n] = (ok + (1 if e.get("ok") else 0), total + 1)
        never = [n for n in names if n not in starts]
        print(f"\nConsensus events (from the nodes): first start {events[0]['t']} on {events[0]['_node']}")
        print(f"  started: {len(starts)}/{len(names)}" + (f"  never started: {', '.join(never)}" if never else ""))
        if starts:
            last_start = max(_parse_t(e["t"]) for e in starts.values())
            print(f"  all starts within {(last_start - t0).total_seconds():.1f} s of the first")
        if finishes:
            last_finish = max(_parse_t(e["t"]) for e in finishes.values())
            reasons = {}
            for e in finishes.values():
                reasons[e.get("reason")] = reasons.get(e.get("reason"), 0) + 1
            print(f"  finished: {len(finishes)}/{len(names)}, last {(last_finish - t0).total_seconds():.1f} s after the first start; "
                  f"reasons: " + ", ".join(f"{k} {v}" for k, v in reasons.items()))
        if merges:
            ok = sum(v[0] for v in merges.values())
            total = sum(v[1] for v in merges.values())
            print(f"  pairwise merges: {total} ({ok} ok, {total - ok} failed), {total / max(1, len(merges)):.1f} per node")
        # ---- run metrics (table / figure columns)
        evil_line0 = next((e for e in reversed(read_lines(log)) if e.get("kind") == "evil_nodes"), None)
        m = run_metrics(events, names, set(evil_line0.get("nodes", [])) if evil_line0 else set())
        if m:
            if evil_line0:
                m["attacks"] = "+".join(evil_line0.get("attacks", []))
            print(f"\n  run: N={m['nodes']}, merges {m['merges']} ({m['merges_per_node']}/node), deltas {m['deltas']}, pulls {m['pulls']}, "
                  f"chain depth {m['chain_depth']}, T_conv {m['t_conv_s'] or 'not converged'} s (95 %: {m['t95_s'] or '-'} s, 99 %: {m['t99_s'] or '-'} s), consensus traffic {m['bytes_total'] / 1024:.1f} KB "
                  f"({m['bytes_per_node'] / 1024:.1f} KB/node), record {m['rec_bytes'] or '?'} B, omega {m['omega'] or '?'}, consistency {m['consistency_pct']} %")
            if getattr(args, "record", False):
                runs = out_dir / "runs.csv"
                if runs.exists():
                    with open(runs, encoding="utf-8") as f:
                        head = f.readline().strip().split(",")
                    if head != RUNS_COLUMNS:   # columns changed: keep the old file aside, start a new one
                        runs.rename(out_dir / f"runs_old_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv")
                new = not runs.exists()
                with open(runs, "a", newline="", encoding="utf-8") as f:
                    w = csv.DictWriter(f, fieldnames=RUNS_COLUMNS)
                    if new:
                        w.writeheader()
                    w.writerow({k: m.get(k, "") for k in RUNS_COLUMNS})
                print(f"  recorded in {runs}")

        # ---- attack test: what the evil nodes did, and who caught them
        evil_line = next((e for e in reversed(read_lines(log)) if e.get("kind") == "evil_nodes"), None)
        evil_nodes = set(evil_line.get("nodes", [])) if evil_line else set()
        acts = [e for e in events if e["ev"] == "evil"]
        found = [e for e in events if e["ev"] == "evidence"]
        if evil_nodes or acts or found:
            print(f"\nAttack test: evil nodes {', '.join(sorted(evil_nodes)) or '-'}" +
                  (f" ({', '.join(evil_line.get('attacks', []))})" if evil_line and evil_nodes else ""))
            for n in sorted(evil_nodes | {e["_node"] for e in acts}):
                mine = [e for e in acts if e["_node"] == n]
                kinds = {}
                for e in mine:
                    kinds[e.get("attack")] = kinds.get(e.get("attack"), 0) + 1
                caught = [e for e in found if e.get("peer") == n]
                first_act = min((_parse_t(e["t"]) for e in mine), default=None)
                first_caught = min((_parse_t(e["t"]) for e in caught), default=None)
                lag = f", first caught {(first_caught - first_act).total_seconds():.1f} s after its first attack" if first_act and first_caught else ""
                print(f"  {n}: {len(mine)} attack(s) " + (", ".join(f"{k} {v}" for k, v in kinds.items()) if kinds else "") +
                      f"; evidence by {len({e['_node'] for e in caught})} node(s) ({', '.join(sorted({e.get('kind') for e in caught})) or 'none'}){lag}")
            honest_hits = [e for e in found if e.get("peer") not in evil_nodes]
            if honest_hits:
                print(f"  false positives: {len(honest_hits)} evidence record(s) about honest nodes: " +
                      ", ".join(sorted({f'{e.get("peer")} ({e.get("kind")}, by {e["_node"]})' for e in honest_hits}))[:600])
            else:
                print("  false positives: none")

        with open(out_dir / "consensus_events.csv", "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["seconds", "time", "node", "event", "detail"])
            for e in events:
                detail = {k: v for k, v in e.items() if k not in ("t", "node", "ev", "session", "_node")}
                w.writerow([f"{(_parse_t(e['t']) - t0).total_seconds():.3f}", e["t"], e["_node"], e["ev"], json.dumps(detail)])
    else:
        print("\nNo consensus events in the node homes" + (" since the last trigger." if since else "."))

    # ---- convergence snapshots
    snaps = [e for e in entries if e.get("kind") == "snapshot"]
    if snaps:
        t0 = _parse_t(events[0]["t"]) if events else _parse_t(snaps[0]["t"])
        n = snaps[-1]["nodes"]
        print(f"\nConvergence snapshots ({len(snaps)}, from {log.name}):")
        print(f"  {'seconds':>8} {'live complete':>14} {'consensus complete':>19} {'avg cons':>9}")
        for s in snaps:
            print(f"  {(_parse_t(s['t']) - t0).total_seconds():>8.0f} {s['complete_live']:>8}/{s['nodes']:<5} "
                  f"{s['complete_consensus']:>12}/{s['nodes']:<6} {s['avg_consensus']:>9}")
        full = next((s for s in snaps if s["complete_consensus"] == s["nodes"]), None)
        if full:
            print(f"  -> full consensus convergence at {(_parse_t(full['t']) - t0).total_seconds():.0f} s "
                  f"(first snapshot where every node holds all {n - 1} others)")
        else:
            last = snaps[-1]
            stuck = sorted(last["per_node"].items(), key=lambda kv: len(kv[1]["missing_consensus"]), reverse=True)[:5]
            print("  -> not converged yet. Furthest behind: " +
                  ", ".join(f"{k} ({len(v['missing_consensus'])} missing)" for k, v in stuck if v["missing_consensus"]))
        with open(out_dir / "convergence.csv", "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(["seconds", "time", "nodes", "running", "complete_live", "complete_consensus", "avg_live", "avg_consensus"])
            for s in snaps:
                w.writerow([f"{(_parse_t(s['t']) - t0).total_seconds():.0f}", s["t"], s["nodes"], s["running"],
                            s["complete_live"], s["complete_consensus"], s["avg_live"], s["avg_consensus"]])
        print(f"\nCSV: {out_dir / 'convergence.csv'}" + (f", {out_dir / 'consensus_events.csv'}" if events else ""))
    else:
        print(f"\nNo snapshots in {log} yet: run  swarm.bat check --watch 15")


def collect_events(args, entries):
    """Every node's consensus events, since the last trigger unless --all / --since."""
    events = []
    for home, info in node_homes(args.rid):
        for e in read_lines(p2p_dir(home) / "consensus_events.jsonl"):
            events.append(e)
    triggers = [e for e in entries if e.get("kind") == "trigger" and e.get("success")]
    # the trigger line is written after the node answered: its own "start" can be a few ms earlier
    since = _parse_t(triggers[-1]["t"]) - timedelta(seconds=5) if triggers and not args.all else None
    if args.since:
        since = _parse_t(args.since if "." in args.since else args.since + ".000")
    if since:
        events = [e for e in events if _parse_t(e["t"]) >= since]
    events.sort(key=lambda e: e["t"])
    return events, since


def viz(args):
    events, since = collect_events(args, read_lines(convergence_log(args.rid)))
    homes = node_homes(args.rid)

    # Direct connections (up / rtt / down). They form before the trigger, so take them from a while before it
    # (homes made by "fresh" hold only this run anyway).
    conn_since = None
    if since and not args.all:
        conn_since = since.timestamp() - args.connections_before * 60
    conns = []
    for home, info in homes:
        for e in read_lines(p2p_dir(home) / "connection_events.jsonl"):
            if conn_since is None or _parse_t(e["t"]).timestamp() >= conn_since:
                conns.append(e)

    # Seed lists from the homes' swarm-node.json (configured links), as one event per pair at the first timestamp
    by_url = {i.get("Url", "").rstrip("/"): i["Name"] for _, i in homes}
    first_t = min([e["t"] for e in events + conns], default=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z"))
    seeds = []
    for _, info in homes:
        for url in info.get("Seeds", []):
            peer = by_url.get(str(url).rstrip("/"))
            if peer and peer != info["Name"]:
                seeds.append({"t": first_t, "node": info["Name"], "ev": "seed", "peer": peer})

    evil_lines = [e for e in read_lines(convergence_log(args.rid)) if e.get("kind") == "evil_nodes"]
    evil = []
    if evil_lines:
        for name in evil_lines[-1].get("nodes", []):
            evil.append({"t": first_t, "node": name, "ev": "evil_node", "attacks": evil_lines[-1].get("attacks", [])})
    events = sorted(events + conns + seeds + evil, key=lambda e: e["t"])
    template = Path(__file__).resolve().parent / "consensus_viz.html"
    slim = [{k: v for k, v in e.items() if k != "session"} for e in events]
    data = json.dumps(slim, separators=(",", ":")).replace("</", "<\\/")
    html = template.read_text(encoding="utf-8").replace("__EVENTS__", data)
    out = Path(args.out) if args.out else group_root(args.rid) / "consensus_viz.html"
    out.write_text(html, encoding="utf-8")
    print(f"{len(events)} consensus events" + (f" since {since.isoformat()}" if since else "") + f" -> {out}")
    if not events:
        print("No events: run a consensus first (swarm.bat consensus), or use --all.")
    elif not args.no_open:
        try:
            import webbrowser
            webbrowser.open(out.resolve().as_uri())
        except Exception:
            pass


# ---------------------------------------------------------------- full test

def http_ok(url, timeout=3):
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            return 200 <= r.status < 400
    except Exception:
        return False


def test(args):
    """Clean convergence test in one go: stop, wipe, prepare, start, wait, trigger, watch, report."""
    print("=== 1/7 stop old nodes"); stop(args)
    if not args.no_build:
        print("=== 2/7 build")
        cmd = ["dotnet", "build", str(ROOT / "ProcessorApplication.sln"), "-c", ENVIRONMENT,
               f"-p:BuildRid={args.rid}", "--nologo", "-v", "minimal"]
        if subprocess.run(cmd).returncode != 0:
            sys.exit("Build failed.")
    else:
        print("=== 2/7 build skipped (--no-build)")

    # one password for the admin-seed files and the consensus trigger
    args.admin_password = admin_password(args)

    print(f"=== 3/7 fresh homes for {args.count} nodes (all node data is wiped)")
    if not args.yes:
        answer = input(f"Wipe databases, keys and Module_Data of the swarm nodes and start a clean test? Type YES: ")
        if answer != "YES":
            sys.exit("Cancelled.")
    args.yes = True
    fresh(args)

    print("=== 4/7 start (one console window per node)")
    args.reingest = "Overwrite"
    start(args)
    homes = node_homes(args.rid)
    if args.only:
        homes = homes[:args.only]
    names = {i["Name"] for _, i in homes}

    need = max(1, min(args.min_known, len(homes) - 1))
    print(f"=== 5/7 wait: every node answers, then knows at least {need} other node(s) (max {args.settle_max:.0f} s)")
    deadline = time.time() + args.settle_max
    while time.time() < deadline:
        up = sum(1 for _, i in homes if http_ok(i["Url"].rstrip("/") + "/api/Heartbeat"))
        linked = sum(1 for h, i in homes if len((known_live(h) & names) - {i["Name"]}) >= need)
        print(f"    {up}/{len(homes)} answering, {linked}/{len(homes)} know {need}+ other node(s)")
        if up == len(homes) and linked == len(homes):
            break
        time.sleep(10)
    print(f"    letting connections settle for {args.settle:.0f} s more")
    time.sleep(args.settle)

    print("=== 6/7 trigger consensus")
    # A busy or dead node must not end the whole bench: try again, and on another node after a couple of tries
    wanted = args.node
    candidates = [i["Name"] for _, i in homes if http_ok(i["Url"].rstrip("/") + "/api/Heartbeat")]
    triggered = False
    for attempt in range(1, 7):
        if not wanted and candidates:
            args.node = candidates[((attempt - 1) // 2) % len(candidates)]
        try:
            consensus(args)
            triggered = True
            break
        except (SystemExit, Exception) as e:
            print(f"    {args.node or 'first node'}: not triggered ({e}); retry in 20 s")
            time.sleep(20)
    args.node = wanted
    if not triggered:
        print("Consensus could not be triggered: this run is skipped.")
        if not getattr(args, "no_archive", False):
            archive(args)
        return

    print(f"=== 7/7 watch convergence every {args.interval:.0f} s (max {args.timeout:.0f} min)")
    log = convergence_log(args.rid)
    end = time.time() + args.timeout * 60
    good = getattr(args, "good_enough", 0.99)
    stall_s = getattr(args, "stall", 4.0) * 60
    last_avg, last_change = None, time.time()
    while True:
        t0 = time.time()
        snap = snapshot(args.rid)
        append_line(log, snap)
        print_snapshot(snap, verbose=False)
        took = time.time() - t0
        if took > 3:
            print(f"    (snapshot took {took:.0f} s)")
        n = snap["nodes"]
        coverage = snap["avg_consensus"] / max(1, n - 1)
        if snap["avg_consensus"] != last_avg:
            last_avg, last_change = snap["avg_consensus"], time.time()
        if snap["complete_consensus"] == n:
            print("Converged.")
            break
        if coverage >= good and time.time() - last_change > 60:
            print(f"Good enough: {coverage:.1%} of all records held and no change for 60 s (--good-enough {good}).")
            break
        if time.time() - last_change > stall_s:
            print(f"Stalled: no change for {stall_s / 60:.0f} min at {coverage:.1%}.")
            break
        if time.time() > end:
            print("Timeout: not converged.")
            break
        time.sleep(args.interval)

    print("\n=== report")
    args.all = False
    args.since = ""
    args.record = not getattr(args, "no_record", False)
    analyze(args)
    if not getattr(args, "no_archive", False):
        print("\n=== archive")
        archive(args)
    print("\nNodes keep running (consoles stay open). Stop them with: swarm.bat stop")


ARCHIVE_P2P_FILES = ("consensus_events.jsonl", "connection_events.jsonl", "rating_events.jsonl")


def archive(args):
    """Copies this run's logs out of the node homes (the next "fresh" wipes them) into one folder per run:
    <archive-dir>/<trigger time>_N<count>[_evil-<attacks>]/  with convergence.log, runs.csv, the replay page and,
    per node (<run>/<node>/) its consensus/connection/rating events, evidence, console logs and settings."""
    rid = args.rid
    homes = node_homes(rid)
    if not homes:
        print("No swarm homes: nothing to archive.")
        return None
    entries = read_lines(convergence_log(rid))
    trig = next((e for e in reversed(entries) if e.get("kind") == "trigger" and e.get("success")), None)
    evil = next((e for e in reversed(entries) if e.get("kind") == "evil_nodes"), None)
    stamp = (trig["t"] if trig else datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z"))[:19].replace(":", "-")
    name = f"{stamp}_N{len(homes)}"
    if evil and evil.get("nodes"):
        name += "_evil-" + "+".join(evil.get("attacks", [])) + f"-{len(evil['nodes'])}"
    if getattr(args, "label", ""):
        name += "_" + re.sub(r"[^A-Za-z0-9_.+-]", "-", args.label)
    root = Path(args.archive_dir) if getattr(args, "archive_dir", "") else group_root(rid) / "runs"
    dest = root / name
    dest.mkdir(parents=True, exist_ok=True)

    def copy(src, dst):
        try:
            if src.is_dir():
                shutil.copytree(src, dst, dirs_exist_ok=True)
            elif src.exists():
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(src, dst)
        except OSError as e:
            print(f"    could not copy {src}: {e}")

    # Flat per node: <run>/<node>/{events .jsonl, console logs, evidence/, settings}
    for home, info in homes:
        nd = dest / info["Name"]
        p2p = p2p_dir(home)
        for f in ARCHIVE_P2P_FILES:
            copy(p2p / f, nd / f)
        ev = p2p / "consensus_evidence"
        if ev.is_dir():
            for f in ev.rglob("*.json"):
                copy(f, nd / "evidence" / f.name)
        for f in (home / "logs").glob("console-*.log") if (home / "logs").is_dir() else []:
            copy(f, nd / f.name)
        copy(home / "swarm-node.json", nd / "swarm-node.json")
        copy(home / "Modules" / "P2PModule" / f"appsettings.P2P.{ENVIRONMENT}.json", nd / f"appsettings.P2P.{ENVIRONMENT}.json")
    for f in ("convergence.log", "runs.csv", "consensus_events.csv", "convergence.csv", "swarm-homes.json"):
        copy(group_root(rid) / f, dest / f)
    try:
        args.out = str(dest / "consensus_viz.html")
        args.no_open = True
        args.all = getattr(args, "all", False)
        args.since = getattr(args, "since", "")
        args.connections_before = getattr(args, "connections_before", 60)
        viz(args)
    except Exception as e:  # the replay is a convenience; the logs are what matters
        print(f"    replay page not written: {e}")
    print(f"Logs of this run archived in {dest}")
    return dest


def bench(args):
    """Runs the whole test for several node counts, a few times each; every run appends a row to runs.csv."""
    counts = [int(c) for c in args.counts.split(",") if c.strip()]
    args.yes = True
    args.no_record = False
    total = len(counts) * args.repeat
    k = 0
    for c in counts:
        for r in range(args.repeat):
            k += 1
            print(f"\n################ bench run {k}/{total}: {c} nodes, repeat {r + 1}/{args.repeat}")
            args.count = c
            test(args)
            args.no_build = True          # build once
            time.sleep(5)
    stop(args)
    print(f"\nBench done. Rows in {group_root(args.rid) / 'runs.csv'}; table and figure: python Build\\swarm\\figures.py")


# ---------------------------------------------------------------- CLI

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rid", default="win-x64", help="platform folder of the build (default win-x64)")
    sub = ap.add_subparsers(dest="command", required=True)

    def prep_options(p):
        p.add_argument("--count", type=int, default=50, help="number of nodes (default 50)")
        p.add_argument("--first-port", type=int, default=6101, help="http port of the first node (default 6101)")
        p.add_argument("--seeds-per-node", type=int, default=2, help="1 = ring only; more = ring + random (default 2)")
        p.add_argument("--extra-seeds", default="", help="comma-separated endpoints every node also seeds")
        p.add_argument("--names", default="", help="comma-separated reserved node names (count = number of names)")
        p.add_argument("--names-file", default="", help="file with one reserved name per line")
        p.add_argument("--prefix", default="swarm", help="generated names <prefix>-001... (default swarm)")
        p.add_argument("--clinic-ids", action="store_true", help="also use each name as the node's Clinic ID")
        p.add_argument("--public", type=int, default=0,
                       help="give the first N nodes their own public Cloudflare Worker tunnel (<name>.<zone>); default 0")
        p.add_argument("--worker-settings", default="",
                       help="json with CloudflareWorkerSettings (ServiceUrl, JoinKey); default: the test nodes' Providerless settings")
        p.add_argument("--random-seed", type=int, default=0, help="same seed choice every run (0 = random)")
        p.add_argument("--admin-name", default="Admin")
        p.add_argument("--admin-surname", default="Swarm")
        p.add_argument("--admin-email", default="")
        p.add_argument("--admin-password", default="", help="else SWARM_ADMIN_PASSWORD, else asked")
        p.add_argument("--evil", type=int, default=0, help="attack test: this many random nodes (never the first) misbehave")
        p.add_argument("--attacks", default="",
                       help="with --evil: comma list of slander, equivocate, forge, corrupt-delta, withhold, mitm")
        p.add_argument("--evil-rate", type=float, default=1.0, help="with --evil: chance each opportunity is used (default 1)")

    def start_options(p):
        p.add_argument("--reingest", choices=["Changed", "Overwrite", "Off"], default="Changed")
        p.add_argument("--window", choices=["normal", "minimized", "hidden"], default="normal",
                       help="console window per node (default normal: open, titled with the node name)")
        p.add_argument("--delay", type=float, default=1.0, help="seconds between node starts (default 1)")
        p.add_argument("--only", type=int, default=0, help="start only the first N homes")

    sub.add_parser("build")
    prep_options(sub.add_parser("prepare"))
    p = sub.add_parser("fresh")
    prep_options(p)
    p.add_argument("--yes", action="store_true", help="don't ask before wiping")
    start_options(sub.add_parser("start"))
    sub.add_parser("stop")
    sub.add_parser("status")
    p = sub.add_parser("up")
    prep_options(p)
    start_options(p)
    p = sub.add_parser("consensus")
    p.add_argument("--node", default="", help="node to trigger on (default: the first one)")
    p.add_argument("--admin-user", default="Admin")
    p.add_argument("--admin-password", default="", help="else SWARM_ADMIN_PASSWORD, else asked")
    p = sub.add_parser("check")
    p.add_argument("--watch", type=float, default=0, help="repeat every N seconds")
    p.add_argument("--until-converged", action="store_true", help="with --watch: stop once every node holds all others")
    p.add_argument("--verbose", action="store_true", help="per-node table on every snapshot")
    p = sub.add_parser("test")
    prep_options(p)
    start_options(p)
    p.add_argument("--no-build", action="store_true", help="use the existing build")
    p.add_argument("--yes", action="store_true", help="don't ask before wiping the node data")
    p.add_argument("--admin-user", default="Admin")
    p.add_argument("--node", default="", help="node that triggers the consensus (default: the first one)")
    p.add_argument("--settle", type=float, default=60, help="extra seconds after every node knows a peer (default 60)")
    p.add_argument("--settle-max", type=float, default=600, help="max seconds to wait for the nodes (default 600)")
    p.add_argument("--min-known", type=int, default=1,
                   help="trigger only once every node knows this many other nodes (default 1 = thin mesh; "
                        "a big number = wait for a full mesh, capped at count-1)")
    p.add_argument("--interval", type=float, default=15, help="seconds between convergence snapshots (default 15)")
    p.add_argument("--timeout", type=float, default=30, help="minutes to wait for convergence (default 30)")
    p.add_argument("--good-enough", type=float, default=0.99, help="stop watching once this share of all records is held and nothing changed for 60 s (default 0.99)")
    p.add_argument("--stall", type=float, default=4, help="stop watching after this many minutes without any change (default 4)")
    p.add_argument("--no-record", action="store_true", help="don't append this run's numbers to <group>/runs.csv")
    p.add_argument("--no-archive", action="store_true", help="don't copy this run's logs out of the node homes")
    p.add_argument("--archive-dir", default="", help="where run folders go (default <group>/runs)")
    p.add_argument("--label", default="", help="extra word in the run folder name")
    p = sub.add_parser("archive")
    p.add_argument("--archive-dir", default="", help="where run folders go (default <group>/runs)")
    p.add_argument("--label", default="", help="extra word in the run folder name")
    p = sub.add_parser("bench")
    prep_options(p)
    start_options(p)
    p.add_argument("--counts", default="5,10,20,50", help="node counts to run (default 5,10,20,50)")
    p.add_argument("--repeat", type=int, default=3, help="runs per node count (default 3)")
    p.add_argument("--no-archive", action="store_true", help="don't copy each run's logs out of the node homes")
    p.add_argument("--archive-dir", default="", help="where run folders go (default <group>/runs)")
    p.add_argument("--label", default="", help="extra word in the run folder names")
    p.add_argument("--no-build", action="store_true", help="use the existing build")
    p.add_argument("--admin-user", default="Admin")
    p.add_argument("--node", default="")
    p.add_argument("--settle", type=float, default=60)
    p.add_argument("--settle-max", type=float, default=600)
    p.add_argument("--min-known", type=int, default=1)
    p.add_argument("--interval", type=float, default=5)
    p.add_argument("--timeout", type=float, default=15)
    p.add_argument("--good-enough", type=float, default=0.99)
    p.add_argument("--stall", type=float, default=4)
    p = sub.add_parser("analyze")
    p.add_argument("--record", action="store_true", help="append this run's numbers to <group>/runs.csv")
    p.add_argument("--all", action="store_true", help="everything in the logs, not only since the last trigger")
    p.add_argument("--since", default="", help="UTC start, e.g. 2026-11-24T02:10:00")

    p = sub.add_parser("viz")
    p.add_argument("--all", action="store_true", help="everything in the logs, not only since the last trigger")
    p.add_argument("--since", default="", help="UTC start, e.g. 2026-11-24T02:10:00")
    p.add_argument("--out", default="", help="output file (default <group>/consensus_viz.html)")
    p.add_argument("--connections-before", type=float, default=60,
                   help="minutes of connection events before the trigger to include (default 60)")
    p.add_argument("--no-open", action="store_true", help="don't open it in the browser")

    args = ap.parse_args()
    if args.command == "up":
        prepare(args)
        start(args)
    else:
        {"build": build, "prepare": prepare, "fresh": fresh, "start": start, "stop": stop, "status": status,
         "consensus": consensus, "check": check, "analyze": analyze, "test": test, "viz": viz, "bench": bench,
         "archive": archive}[args.command](args)


LONG_COMMANDS = {"test", "bench", "build", "fresh", "up"}


def beep(ok):
    """Sound at the end of a long command: two rising tones = done, three low ones = failed.
    Off with SWARM_BEEP=0."""
    if os.environ.get("SWARM_BEEP", "1") == "0":
        return
    try:
        import winsound
        tones = [(880, 150), (1320, 250)] if ok else [(440, 300), (330, 300), (220, 500)]
        for freq, ms in tones:
            winsound.Beep(freq, ms)
    except Exception:
        try:
            sys.stdout.write("\a" if ok else "\a\a\a")
            sys.stdout.flush()
        except Exception:
            pass


if __name__ == "__main__":
    command = next((a for a in sys.argv[1:] if not a.startswith("-")), "")
    long_run = command in LONG_COMMANDS or (command == "check" and "--watch" in sys.argv)
    ok = False
    try:
        main()
        ok = True
    except SystemExit as e:
        ok = e.code in (None, 0)
        raise
    finally:
        if long_run:
            beep(ok)
