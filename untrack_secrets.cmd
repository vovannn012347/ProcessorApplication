@echo off
rem Stops tracking files with secrets. Files stay on disk; nothing is committed or pushed.
cd /d "%~dp0"
git rm --cached --ignore-unmatch -- "ProcessorApplication.zip" "ProcessorApplication/configFiles/medicalsystem.db-wal" "ProcessorApplication/configFiles/medicalsystem.db-shm" "ProcessorApplication/configFiles/Tracker1/medicalsystem.db-wal" "ProcessorApplication/configFiles/Tracker1/medicalsystem.db-shm" "ProcessorApplication/configFiles/Client1/medicalsystem.db-wal" "ProcessorApplication/configFiles/Client1/medicalsystem.db-shm"
git rm -r --cached --ignore-unmatch -- "Modules/FileTransferModule/configFiles/Client1/Module_Data/P2P/identity" "Modules/P2PModule/configFiles/Client1/Module_Data/P2P/identity"
git add .gitignore
echo.
git status --short
echo.
echo Check the list above, then: git commit -m "Stop tracking secrets" and git push
pause
