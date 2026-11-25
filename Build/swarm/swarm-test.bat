@echo off
rem Swarm convergence test in one go (see SWARM_TEST.md next to this file):
rem   stop old nodes, build, fresh homes, start (one console per node), wait until they are connected,
rem   trigger the consensus, watch convergence, print the report.
rem
rem   swarm-test.bat                      50 nodes (default)
rem   swarm-test.bat --count 20           20 nodes
rem   swarm-test.bat --count 20 --no-build --yes --admin-password Secret-123
rem   swarm-test.bat --help               all options
rem
rem Results: bin\win-x64\TrackerSwarm\convergence.log, convergence.csv, consensus_events.csv

call "%~dp0swarm.bat" test %*
exit /b %ERRORLEVEL%
