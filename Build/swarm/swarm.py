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
    start     start the nodes (http only, one console per node, minimized by default)
    stop      stop the nodes started by "start"
    status    list the homes and which nodes are running
    up        prepare + start
Only the Python standard library is used.
"""
import argparse
import getpass
import json
import os
import random
import re
import shutil
import socket
import subprocess
import sys
import time
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

        write_json(home / "Modules" / "P2PModule" / f"appsettings.P2P.{ENVIRONMENT}.json", {
            "P2PSettings": {
                "EnforcedNodeIdentifier": node["Name"],
                # advertised endpoints are kept without a scheme (https is tried first, then http)
                "Endpoints": [f"localhost:{node['Port']}"],
                "SeedEndpoints": node["Seeds"],
            }
        })
        clinic = home / "Modules" / "ProviderlessModule" / f"appsettings.Providerless.{ENVIRONMENT}.json"
        if args.clinic_ids:
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

    write_json(group_root(args.rid) / "swarm-homes.json",
               [{"Name": n["Name"], "Port": n["Port"], "Url": n["Url"]} for n in nodes])

    print(f"Swarm homes in {root}: {created} new, {updated} updated; {seeded} will create their Admin on first start.")
    print(f"Nodes {names[0]} .. {names[-1]}, ports {args.first_port}..{args.first_port + count - 1}, "
          f"{seeds_per_node} seed(s) each" + (f" + {', '.join(extra)}" if extra else "") + ".")
    if presets:
        print("Presets from: " + ", ".join(str(p.relative_to(ROOT)) for p in presets))


# ---------------------------------------------------------------- fresh

def wipe_node_data(home):
    """Everything the node created itself; its settings override, swarm-node.json and presets stay."""
    for p in home.rglob("*"):
        if p.is_file() and re.search(r"\.db(-wal|-shm)?$", p.name):
            p.unlink()
    for d in ("Module_Data", "data", ".build", "logs"):
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


def alive_pids(rid):
    entries = read_json(pid_file(rid), []) or []
    return [e for e in entries if pid_alive(int(e["Pid"]))]


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
        print(f"{info['Name']:<14} {info['Url']:<24} {has_db:<6} {state}")
    print(f"{len(homes)} home(s), {len(running)} running.")


def build(args):
    sln = ROOT / "ProcessorApplication.sln"
    cmd = ["dotnet", "build", str(sln), "-c", ENVIRONMENT, f"-p:RuntimeIdentifier={args.rid}", "--nologo", "-v", "minimal"]
    print(" ".join(cmd))
    sys.exit(subprocess.run(cmd).returncode)


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
        p.add_argument("--random-seed", type=int, default=0, help="same seed choice every run (0 = random)")
        p.add_argument("--admin-name", default="Admin")
        p.add_argument("--admin-surname", default="Swarm")
        p.add_argument("--admin-email", default="")
        p.add_argument("--admin-password", default="", help="else SWARM_ADMIN_PASSWORD, else asked")

    def start_options(p):
        p.add_argument("--reingest", choices=["Changed", "Overwrite", "Off"], default="Changed")
        p.add_argument("--window", choices=["minimized", "normal", "hidden"], default="minimized")
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

    args = ap.parse_args()
    if args.command == "up":
        prepare(args)
        start(args)
    else:
        {"build": build, "prepare": prepare, "fresh": fresh, "start": start, "stop": stop, "status": status}[args.command](args)


if __name__ == "__main__":
    main()
