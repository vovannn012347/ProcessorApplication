@echo off
rem Swarm test nodes - thin wrapper around swarm.py (Python 3, standard library only).
rem
rem   swarm.bat build                                  build configuration NodeTrackerSwarm (win-x64)
rem   swarm.bat fresh --count 50                       wipe every node's data, make 50 fresh homes (asks for the admin password)
rem   swarm.bat start --reingest Overwrite             first start: apply every settings file
rem   swarm.bat start                                  later starts
rem   swarm.bat status                                 which nodes exist / run
rem   swarm.bat stop                                   stop all swarm nodes
rem   swarm.bat up --count 50 --reingest Overwrite     prepare + start in one go
rem   swarm.bat prepare --count 50 --seeds-per-node 3  update the homes' settings (keeps their data)
rem   swarm.bat consensus                              sign in to the first node, press "Trigger consensus"
rem   swarm.bat check                                  convergence snapshot of every node -> convergence.log
rem   swarm.bat check --watch 15 --until-converged     repeat every 15 s until every node holds all others
rem   swarm.bat analyze                                report + convergence.csv / consensus_events.csv
rem   swarm.bat viz                                    consensus_viz.html: the run replayed as a growing graph
rem   swarm.bat bench --counts 5,10,20,50 --repeat 3   whole test per count, rows in runs.csv (then: py Build\swarm\figures.py)
rem   swarm.bat test --count 20                        the whole test in one go (same as swarm-test.bat)
rem   swarm.bat <command> --help                       all options
rem
rem Whole convergence test: swarm-test.bat (see SWARM_TEST.md)
rem
rem Admin password: --admin-password <pw>, or set SWARM_ADMIN_PASSWORD, or it is asked once.

setlocal
set "PY="
where py >nul 2>nul && set "PY=py -3"
if not defined PY (
    where python >nul 2>nul && set "PY=python"
)
if not defined PY (
    echo Python 3 was not found. Install it from python.org, or put python.exe on PATH.
    exit /b 1
)
%PY% "%~dp0swarm.py" %*
exit /b %ERRORLEVEL%
