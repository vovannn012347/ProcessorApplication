# Swarm convergence test

Starts N full nodes on this PC, has them find each other, runs one consensus round and measures how fast every node ends up knowing every other node.
Everything is in `Build\swarm\`. Run the commands from a normal Command Prompt (or Git Bash) in the repository root.

## Before you start

- **Python 3** on PATH (`py` or `python`); only the standard library is used.
- **.NET SDK** for the build (the same one you build the app with).
- **Memory:** about 80 MB per node. 20 nodes ≈ 1.6 GB, 50 nodes ≈ 4 GB.
- **Ports:** 6101 and up (one per node) must be free.
- **Time:** the consensus session is cut at **00:00 UTC (02:00 Kyiv)**. Don't start the test between ~01:30 and 02:00 Kyiv time.
- **Admin password:** one password for every swarm node (used for each node's first admin, and to trigger the consensus). It must meet the normal password rules.

## Run it (one command)

```bat
Build\swarm\swarm-test.bat --count 20
```

It asks for the admin password and for `YES` before wiping the old swarm data, then does everything itself:

| Step | What happens |
|---|---|
| 1. stop | Stops swarm nodes still running from an earlier run. |
| 2. build | Builds configuration `NodeTrackerSwarm` (skip with `--no-build`). |
| 3. fresh | Wipes every node's data and makes N clean node homes (own keys, own identity, admin from `admin-seed.json`). |
| 4. start | Starts every node in its **own console window**, titled with the node name (`swarm-001 - ProcessorApplication`). |
| 5. wait | Waits until every node answers and knows at least one other node (`--min-known N` for more), then 60 s more for connections to settle. |
| 6. trigger | Signs in to the first node as Admin and presses **Trigger consensus**. The other nodes join through start pings. |
| 7. watch | Every 15 s: a convergence snapshot of every node, written to `convergence.log`, until all nodes hold all others (max 30 min). |

At the end it prints the report (see below). The nodes keep running so you can look at them; stop them with `Build\swarm\swarm.bat stop`.

Useful options:
- `--no-build`: use the existing build.
- `--yes`: don't ask before wiping.
- `--admin-password <pw>` (or environment variable `SWARM_ADMIN_PASSWORD`): don't ask for the password.
- `--window minimized`: start the consoles minimized (default: open). `--window hidden` costs the least CPU.
- `--log-level Information` (default): the full console log. `--log-level Warning` prints only warnings and errors, which saves CPU with 20+ nodes on one PC (every line is drawn in its window). The measurements come from the `*.jsonl` event logs, which are written at any level.
- `--settle 120`, `--interval 10`, `--timeout 45`: longer settle time, more frequent snapshots, longer maximum wait.
- `--min-known 4`: trigger only when every node already knows 4 other nodes (with 5 nodes: a full mesh). The default 1 starts from a thin mesh, which is the harder case for consensus.

## Run it step by step (same thing, by hand)

```bat
Build\swarm\swarm.bat build
Build\swarm\swarm.bat fresh --count 20 --yes
Build\swarm\swarm.bat start --reingest Overwrite
rem wait until the consoles calm down (2-5 minutes)
Build\swarm\swarm.bat consensus
Build\swarm\swarm.bat check --watch 15 --until-converged
Build\swarm\swarm.bat analyze
Build\swarm\swarm.bat stop
```

`swarm.bat check --verbose` shows one line per node: how many other nodes it knows (live / consensus), its consensus day and its last consensus event.

## Public endpoints (optional)

By default the swarm nodes only know each other as `localhost:<port>` (no tunnels). To also test the public path, give the first N nodes their own Cloudflare Worker tunnel:

```bat
Build\swarm\swarm-test.bat --count 20 --public 3
```

- **Addresses:** each of those nodes claims `<name>.<zone>` (e.g. `swarm-001.processor-app.pp.ua`) and advertises it to the mesh next to `localhost:<port>`.
- **Worker settings:** the tunnel needs the Worker's `ServiceUrl` and `JoinKey`. They're taken from `Modules\ProviderlessModule\appsettings.Providerless.NodeTracker1Debug.json` (your test tracker's settings), or from a file given with `--worker-settings <json>`. A node's own `TunnelToken` is never copied.
- **cloudflared:** copied into each public node's home from `configFiles\...\Module_Data\Portal\bin` when available, otherwise each node downloads it (~55 MB).
- **Cost:** each public node runs its own `cloudflared` (about 30–50 MB RAM) and owns one subdomain in your Cloudflare zone. Keep N small (3–5).
- **Kept by `fresh`:** `Module_Data\Portal` survives, because its provisioning key owns the node's subdomain. A new key couldn't claim the same name again for 30 days.
- **Check:** `swarm.bat status` marks public nodes. In a public node's console, look for the tunnel URL and for `[P2P Endpoints] Advertised endpoints changed: [localhost:…] -> [localhost:…, swarm-001.…]`.

## Logs

- **Console:** the app's own lines.
- **`<node home>\logs\console-<date>.log`:** a copy of everything the node's console window shows, so closing the window doesn't lose the log. Turn it off with `--no-console-log` or `PROCESSOR_CONSOLE_LOG=0`.
- **`<node home>\logs\sql-<date>.log`:** every SQL statement EF Core runs (migrations, settings writes, logins). Only EF warnings and errors also show on the console. Turn the file off with `Logging:SqlFile:Enabled = false` in `appsettings.json`.
- **Consensus events:** `Module_Data\P2P\consensus_events.jsonl` (see below).

## What is measured

Two views per node, read straight from the node homes (`bin\win-x64\TrackerSwarm\nodes\<name>\Module_Data\P2P\`):
- **live**: the nodes it knows right now (its peer store, `consensus_latest\currentIndex`), filled by gossip and by the consensus.
- **consensus**: the nodes in its newest consensus day (`history\<yyyy-MM-dd>\peers`), i.e. what the consensus round agreed on.

A node is **complete** when it holds all N−1 other nodes. The test has **converged** when every node is complete in the consensus view.

Each node also writes its consensus events to `Module_Data\P2P\consensus_events.jsonl`:
- `start`: a session began (`initiator`, or `joined` with the node that pinged it);
- `merge`: a pairwise merge with a peer (`ok` true/false);
- `queue_empty`: no more peers to visit;
- `finish`: the session ended (`reason`: inactivity, midnight or error; `known`: how many nodes it knew).


### Attack runs

`swarm.bat test --count 20 --evil 6 --attacks slander,equivocate,forge,corrupt-delta,withhold --evil-mode coordinated`

- `--evil N`: N random nodes (never the first) misbehave. `--evil-rate` = the chance each opportunity is used (default 1).
- `--evil-mode random` (default): each evil node gets its own random subset of `--attacks`; they act independently.
- `--evil-mode all`: every evil node uses every attack, independently.
- `--evil-mode coordinated`: every evil node uses every attack and they collude: they slander and forge the same victims (a third of the honest nodes), report each other at trust 100, and never withhold from, forge or replay against each other.
- On every opportunity (a swap or pull answer) an evil node uses ONE of its record attacks (withhold, slander, equivocate, forge), picked at random; `corrupt-delta` and `mitm` have their own opportunities.
- The assignment is printed at prepare, written to `convergence.log` (`evil_nodes`: mode, per-node attacks, shared victims) and shown by `analyze`. The archive folder and the `attacks` column of `runs.csv` carry the mode.

## Logs of every run (`runs\`)

`fresh` wipes the node homes, so after every `test` / `bench` run its logs are copied to
`bin\win-x64\TrackerSwarm\runs\<trigger time>_N<count>[_evil-<attacks>-<k>]\`:
`convergence.log`, `runs.csv`, the replay page `consensus_viz.html`, and per node `<name>\` its
`consensus_events.jsonl`, `connection_events.jsonl`, `rating_events.jsonl`, `evidence\`, console logs and settings. `--archive-dir D:\somewhere` puts the run folders elsewhere, `--label word` adds a word
to the name, `--no-archive` skips it; `swarm.bat archive` copies the current homes by hand.
`figures.py` takes the reputation panel from the newest `runs\*_evil-*` folder (or `--attack-run <folder>`).

## Replay as a graph (`swarm.bat viz`)

Writes `consensus_viz.html` next to `convergence.log` and opens it: the run as a 3D mesh you can turn.
Links: grey = seeded (from the homes' seed lists), red = live connection (`connection_events.jsonl`: up / rtt / down,
logged from the heartbeat), deep green = consensus merge, yellow = delta push (direction ignored).

One step per event (connection up/down, start ping, merge, delta, finish). The layout is t-SNE in 3D, run a few
small iterations per step (slider) from random positions:
- affinity of a pair = sum of the weights of the links it has so far (sliders; defaults seed 1, delta 2,
  connection 3, merge 4), a connection scaled by 2·median/(median + its ping), so low-latency pairs pull harder;
- everyone repels through the Student-t kernel, a small gravity keeps stray nodes in view.
So nodes drift slowly along their seed links first, then harder along connections and merges as they appear.

Node size = the share of the swarm the node holds (replayed from merges and deltas); green ring = holds all.
`--all` / `--since` as for analyze, `--connections-before 60` = minutes of connection events before the trigger.
Other runs can be loaded into the page (file picker, or drop the .jsonl files on the plate).

## The report (`swarm.bat analyze`)

By default the report covers everything since the last trigger (`--all` for the whole log, `--since 2026-11-24T02:10:00` for a UTC start time).

- **Consensus events:**
  - first start and which node;
  - how many nodes started, and which never did;
  - how spread out the starts were;
  - when the last node finished, and why the sessions ended;
  - the number of pairwise merges (ok / failed).
- **Convergence snapshots:** a table of seconds since the first start against complete nodes (live and consensus) and the average number of nodes known. Then either the time to full convergence, or the nodes furthest behind.
- **CSV files for charts** (in `bin\win-x64\TrackerSwarm\`):
  - `convergence.csv`: one row per snapshot (seconds, complete nodes, averages), e.g. for a "nodes converged over time" chart;
  - `consensus_events.csv`: every event of every node, with seconds since the first start.

## Notes

- **Swarm-only settings** (`Modules\P2PModule\appsettings.P2P.NodeTrackerSwarm.json`):
  - the nodes write their peer store to disk every 15 s, with no limit on new nodes per write;
  - normal nodes write it every 5 minutes, with at most 10 new nodes per write as Sybil protection. With those defaults the "live" view would lag by up to ~25 minutes for 50 nodes.
- **`convergence.log` is kept between runs.** The report picks the part after the last trigger. Delete the file for a completely clean history.
- **Old swarm homes:** `fresh` wipes the node data but keeps each node's name, port and seeds. Change the node count by running `fresh` with another `--count`.
- **On Linux:** `swarm.py` runs too (`python3 Build/swarm/swarm.py --rid linux-x64 test --count 20`). There are no console windows; each node writes to `console.log` in its home.
