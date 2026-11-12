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
rem   swarm.bat <command> --help                       all options
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
