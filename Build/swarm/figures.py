#!/usr/bin/env python3
"""
Table and figure for the dissertation from swarm runs (chapter 4).

    python Build/swarm/figures.py                 table + figure from the default swarm folder
    python Build/swarm/figures.py --group <dir>   another swarm folder (the one with runs.csv / convergence.log)

Reads:
    <group>/runs.csv            one row per run (swarm.bat test / bench / analyze --record)
    <group>/nodes/*/Module_Data/P2P/rating_events.jsonl, consensus_events.jsonl
                                the LAST run in the node homes, for the reputation panel (an attack run:
                                swarm.bat test --evil 2 --attacks ...)
    <group>/convergence.log     which nodes were evil, when the consensus was triggered
Writes:
    <group>/table_4_x.csv, table_4_x.md   one row per node count: mean +- sd over the runs without attackers
    <group>/fig_4_4.png (300 dpi), fig_4_4.svg
        (a) full-convergence time vs number of nodes, mean +- sd
        (b) local reputation of evil nodes vs honest nodes over time during the attack run

Needs matplotlib:  py -m pip install matplotlib
"""
import argparse
import csv
import json
import math
import statistics
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HONEST = "#257de0"   # categorical slot 1 (blue)
EVIL = "#f47230"     # categorical slot 2 (orange); also dashed, so it never relies on colour alone
INK, INK2, GRID = "#191919", "#666666", "#e3e3e3"


def parse_t(t):
    return datetime.strptime(t.replace("Z", ""), "%Y-%m-%dT%H:%M:%S.%f").replace(tzinfo=timezone.utc)


def read_lines(path):
    out = []
    if path.exists():
        for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
            line = line.strip()
            if line:
                try:
                    out.append(json.loads(line))
                except ValueError:
                    pass
    return out


def num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def mean_sd(values):
    values = [v for v in values if v is not None]
    if not values:
        return None, None
    return statistics.mean(values), (statistics.stdev(values) if len(values) > 1 else 0.0)


def fmt(m, s, digits=2):
    if m is None:
        return "-"
    return f"{m:.{digits}f} ± {s:.{digits}f}" if s else f"{m:.{digits}f}"


# ---------------------------------------------------------------- table

def load_runs(group):
    path = group / "runs.csv"
    if not path.exists():
        return []
    with open(path, encoding="utf-8") as f:
        return list(csv.DictReader(f))


def table(group, runs):
    clean = [r for r in runs if (num(r.get("evil")) or 0) == 0]
    by_n = {}
    for r in clean:
        by_n.setdefault(int(num(r["nodes"])), []).append(r)
    rows = []
    for n in sorted(by_n):
        rs = by_n[n]
        t = mean_sd([num(r["t_conv_s"]) for r in rs])
        mpn = mean_sd([num(r["merges_per_node"]) for r in rs])
        depth = mean_sd([num(r["chain_depth"]) for r in rs])
        kb = mean_sd([(num(r["bytes_per_node"]) or 0) / 1024 for r in rs])
        omega = mean_sd([num(r["omega"]) for r in rs])
        cons = mean_sd([num(r["consistency_pct"]) for r in rs])
        rows.append({
            "N": n, "runs": len(rs),
            "merges_per_node": fmt(*mpn, 1), "chain_depth": fmt(*depth, 1),
            "t_conv": fmt(*t, 2), "kb_per_node": fmt(*kb, 1), "omega": fmt(*omega, 1),
            "consistency": fmt(*cons, 1),
            "_t": t,
        })
    heads = ["Кількість вузлів (N)", "Прогонів", "Парних злиттів на вузол", "Глибина ланцюга підписів (раунди)",
             "Час повної збіжності Tconv, с", "Трафік консенсусу на вузол, КБ", "Накладний коефіцієнт Ω", "Досягнута узгодженість, %"]
    keys = ["N", "runs", "merges_per_node", "chain_depth", "t_conv", "kb_per_node", "omega", "consistency"]
    with open(group / "table_4_x.csv", "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f, delimiter=";")
        w.writerow(heads)
        for r in rows:
            w.writerow([r[k] for k in keys])
    md = ["| " + " | ".join(heads) + " |", "|" + "---|" * len(heads)]
    md += ["| " + " | ".join(str(r[k]) for k in keys) + " |" for r in rows]
    (group / "table_4_x.md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print("\n".join(md) if rows else "No runs without attackers in runs.csv yet (swarm.bat bench).")
    return rows


# ---------------------------------------------------------------- reputation series

def reputation(group):
    """Per subject node: time series of its mean local score across the other nodes, for the last run."""
    log = read_lines(group / "convergence.log")
    evil_line = next((e for e in reversed(log) if e.get("kind") == "evil_nodes"), None)
    evil = set(evil_line.get("nodes", [])) if evil_line else set()
    trig = next((e for e in reversed(log) if e.get("kind") == "trigger" and e.get("success")), None)
    t0 = parse_t(trig["t"]) if trig else None

    events, first_attack = [], None
    for home in sorted((group / "nodes").glob("*")):
        p2p = home / "Module_Data" / "P2P"
        for e in read_lines(p2p / "rating_events.jsonl"):
            if e.get("peer") and e.get("score") is not None:
                e["_obs"] = home.name
                events.append(e)
        for e in read_lines(p2p / "consensus_events.jsonl"):
            if e.get("ev") == "evil":
                t = parse_t(e["t"])
                if t0 is None or t >= t0:
                    first_attack = t if first_attack is None or t < first_attack else first_attack
    if t0:
        events = [e for e in events if parse_t(e["t"]) >= t0]
    if not events:
        return None
    events.sort(key=lambda e: e["t"])
    t0 = t0 or parse_t(events[0]["t"])
    latest = {}          # (observer, subject) -> score
    series = {}          # subject -> [(seconds, mean score over observers)]
    for e in events:
        if e["_obs"] == e["peer"]:
            continue
        latest[(e["_obs"], e["peer"])] = float(e["score"])
        scores = [v for (o, s), v in latest.items() if s == e["peer"]]
        series.setdefault(e["peer"], []).append(((parse_t(e["t"]) - t0).total_seconds(), statistics.mean(scores)))
    return {"series": series, "evil": evil,
            "attack_s": (first_attack - t0).total_seconds() if first_attack else None}


# ---------------------------------------------------------------- figure

def figure(group, rows, rep):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        sys.exit("matplotlib is missing: py -m pip install matplotlib")

    plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 10, "axes.edgecolor": INK2, "axes.labelcolor": INK,
                         "xtick.color": INK2, "ytick.color": INK2, "axes.spines.top": False, "axes.spines.right": False})
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(11, 4.2))

    # (a) convergence time vs N
    pts = [(r["N"], r["_t"][0], r["_t"][1]) for r in rows if r["_t"][0] is not None]
    if pts:
        xs, ms, sds = zip(*pts)
        ax1.errorbar(xs, ms, yerr=sds, color=HONEST, lw=2, marker="o", ms=7, capsize=4, elinewidth=1.2,
                     markeredgecolor="white", markeredgewidth=1.5, zorder=3)
        for x, m in ((xs[0], ms[0]), (xs[-1], ms[-1])):
            ax1.annotate(f"{m:.1f} с", (x, m), textcoords="offset points", xytext=(0, 10), ha="center", color=INK, fontsize=9)
        ax1.set_xticks(xs)
        ax1.set_ylim(bottom=0, top=max(m + s for m, s in zip(ms, sds)) * 1.25)
    else:
        ax1.text(0.5, 0.5, "немає прогонів без атак\n(swarm.bat bench)", ha="center", va="center", color=INK2, transform=ax1.transAxes)
    ax1.set_xlabel("Кількість вузлів N")
    ax1.set_ylabel("Час повної збіжності, с")
    ax1.set_title("а) збіжність епідемічного консенсусу", loc="left", color=INK, fontsize=10)
    ax1.grid(axis="y", color=GRID, lw=0.8)
    ax1.set_axisbelow(True)

    # (b) reputation under attack
    if rep and rep["series"]:
        honest = {s: v for s, v in rep["series"].items() if s not in rep["evil"]}
        evil = {s: v for s, v in rep["series"].items() if s in rep["evil"]}
        if honest:
            # mean of the honest nodes on one time grid (each node's last value carried forward)
            times = sorted({t for v in honest.values() for t, _ in v})
            mean_line = []
            for t in times:
                vals = []
                for v in honest.values():
                    last = [y for x, y in v if x <= t]
                    if last:
                        vals.append(last[-1])
                mean_line.append((t, statistics.mean(vals)))
            ax2.plot([t for t, _ in mean_line], [y for _, y in mean_line], color=HONEST, lw=2, drawstyle="steps-post")
            ax2.annotate("чесні вузли (середнє)", mean_line[-1], textcoords="offset points", xytext=(-4, 8), ha="right", color=INK, fontsize=9)
        for name, v in sorted(evil.items()):
            ax2.plot([t for t, _ in v], [y for _, y in v], color=EVIL, lw=2, ls=(0, (5, 3)), drawstyle="steps-post")
            ax2.annotate(f"{name} (атакує)", v[-1], textcoords="offset points", xytext=(-4, 7), ha="right", color=INK, fontsize=9)
        if rep["attack_s"] is not None:
            ax2.axvline(rep["attack_s"], color=INK2, lw=1, ls=":")
            ax2.annotate("перша атака", (rep["attack_s"], ax2.get_ylim()[1]), textcoords="offset points", xytext=(4, -12), color=INK2, fontsize=9)
        ax2.axhline(0, color=GRID, lw=1)
    else:
        ax2.text(0.5, 0.5, "немає даних прогону з атаками\n(swarm.bat test --evil 2 --attacks ...)", ha="center", va="center",
                 color=INK2, transform=ax2.transAxes)
    ax2.set_xlabel("Час від запуску консенсусу, с")
    ax2.set_ylabel("Локальний рейтинг вузла")
    ax2.set_title("б) рейтинг вузлів під час Byzantine-атак", loc="left", color=INK, fontsize=10)
    ax2.grid(axis="y", color=GRID, lw=0.8)
    ax2.set_axisbelow(True)

    fig.tight_layout()
    for ext, dpi in (("png", 300), ("svg", None)):
        fig.savefig(group / f"fig_4_4.{ext}", dpi=dpi, bbox_inches="tight")
    print(f"Figure: {group / 'fig_4_4.png'} (+ .svg)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--group", default="", help="swarm folder (default bin/<rid>/TrackerSwarm)")
    ap.add_argument("--rid", default="win-x64")
    args = ap.parse_args()
    group = Path(args.group) if args.group else ROOT / "bin" / args.rid / "TrackerSwarm"
    if not group.is_dir():
        sys.exit(f"No swarm folder at {group}")
    rows = table(group, load_runs(group))
    figure(group, rows, reputation(group))
    print(f"Table: {group / 'table_4_x.csv'} (+ .md)")


if __name__ == "__main__":
    main()
