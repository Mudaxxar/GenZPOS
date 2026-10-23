@echo off
rem ======================================================================
rem  GenZ-POS - restore a backup (.bak) over the current data.
rem  1. Close GenZ-POS on EVERY computer.
rem  2. On the computer that has SQL Server: right-click -> Run as administrator.
rem ======================================================================
setlocal EnableExtensions
title GenZ-POS - restore a backup
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo  Please right-click restore.cmd and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

where sqlcmd >nul 2>&1
if errorlevel 1 (
    echo.
    echo  "sqlcmd" was not found. It comes with SQL Server.
    echo  Run this on the computer that has SQL Server, or install the
    echo  "SQL Server Command Line Utilities" from Microsoft.
    echo.
    pause
    exit /b 1
)

if not exist "%~dp0restore.sql" (
    echo  restore.sql is missing next to restore.cmd.
    pause
    exit /b 1
)

rem ---- Settings from appsettings.json (server, database, backup folder) ----
set "SERVER=."
set "DB=Blazor"
set "BACKUP=C:\GenZ-POS-Backups"
if exist "%~dp0appsettings.json" (
    for /f "usebackq tokens=1,2,3 delims=|" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $c = Get-Content -Raw -LiteralPath '%~dp0appsettings.json' | ConvertFrom-Json; $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $c.ConnectionString; '{0}|{1}|{2}' -f $b.DataSource, $b.InitialCatalog, $c.BackupFolder } catch { }"`) do (
        if not "%%A"=="" set "SERVER=%%A"
        if not "%%B"=="" set "DB=%%B"
        if not "%%C"=="" set "BACKUP=%%C"
    )
)

tasklist /FI "IMAGENAME eq GenZ-POS.exe" 2>nul | find /i "GenZ-POS.exe" >nul
if not errorlevel 1 (
    echo.
    echo  GenZ-POS is still open on this computer. Close it first, then run restore.cmd again.
    echo.
    pause
    exit /b 1
)

echo.
echo  Database : %DB%  on SQL Server "%SERVER%"
echo  Backups  : %BACKUP%
echo.
echo  Drag the backup file (.bak) into this window, then press Enter.
echo  (Backups are normally in %BACKUP%)
echo.
set "BAK="
set /p "BAK=Backup file: "
if not defined BAK goto :cancelled
set "BAK=%BAK:"=%"
if not exist "%BAK%" (
    echo.
    echo  File not found: %BAK%
    goto :failed
)

echo.
echo  WARNING: ALL current data in GenZ-POS will be replaced by this backup:
echo     %BAK%
echo  Anything entered after that backup was made will be lost.
echo  (A copy of the current data is saved first, just in case.)
echo.
set "ANSWER="
set /p "ANSWER=Type YES and press Enter to continue: "
if /i not "%ANSWER%"=="YES" goto :cancelled

for /f %%T in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "TS=%%T"
if not exist "%BACKUP%" mkdir "%BACKUP%"

rem SQL Server reads the file with its own account: make sure it is in the backup folder it may read.
for %%I in ("%BAK%") do (
    set "BAKDIR=%%~dpI"
    set "BAKNAME=%%~nxI"
)
if /i not "%BAKDIR%"=="%BACKUP%\" (
    echo.
    echo  Copying the backup into %BACKUP% ...
    copy /y "%BAK%" "%BACKUP%\restore_%TS%_%BAKNAME%" >nul
    if errorlevel 1 (
        echo  Could not copy the file into %BACKUP%.
        goto :failed
    )
    set "BAK=%BACKUP%\restore_%TS%_%BAKNAME%"
)

echo.
echo  Saving a copy of the current data first...
sqlcmd -S "%SERVER%" -E -d master -b -Q "IF DB_ID(N'%DB%') IS NOT NULL BACKUP DATABASE [%DB%] TO DISK = N'%BACKUP%\%DB%_%TS%_before-restore.bak' WITH INIT, CHECKSUM"
if errorlevel 1 (
    echo.
    echo  Could not save a copy of the current data, so nothing was changed.
    echo  If the message above says "Operating system error 5", run setup.cmd as administrator first.
    goto :failed
)

rem Quotes in the path would break the SQL text: double them.
set "BAKSQL=%BAK:'=''%"

echo.
echo  Restoring... (this can take a minute)
sqlcmd -S "%SERVER%" -E -d master -b -v BAK="%BAKSQL%" DB="%DB%" -i "%~dp0restore.sql"
if errorlevel 1 (
    echo.
    echo  RESTORE FAILED. Your data was not replaced ^(or the copy saved above can be restored^).
    goto :failed
)

echo.
echo  Done. Start GenZ-POS again on each computer.
echo.
pause
exit /b 0

:cancelled
echo.
echo  Cancelled. Nothing was changed.
echo.
pause
exit /b 1

:failed
echo.
echo  Take a photo of this window and send it to your technician.
echo  The current data was saved as %BACKUP%\%DB%_%TS%_before-restore.bak (if that step ran).
echo.
pause
exit /b 1
