@echo off
rem ======================================================================
rem  GenZ-POS - one-time setup on the shop PC.
rem  Right-click this file and choose "Run as administrator".
rem  Safe to run again (e.g. after moving the backup folder).
rem ======================================================================
setlocal EnableExtensions
title GenZ-POS setup
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo  Please right-click setup.cmd and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

set "APPDIR=%~dp0"
set "BACKUP=C:\GenZ-POS-Backups"

rem Use the backup folder from appsettings.json if it has one.
if exist "%APPDIR%appsettings.json" (
    for /f "usebackq delims=" %%F in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $c = Get-Content -Raw -LiteralPath '%APPDIR%appsettings.json' | ConvertFrom-Json; if ($c.BackupFolder) { $c.BackupFolder } } catch { }"`) do set "BACKUP=%%F"
)

rem Find the SQL Server service (default instance, or SQL Express named instance).
set "SQLSVC="
sc query MSSQLSERVER >nul 2>&1 && set "SQLSVC=MSSQLSERVER"
if not defined SQLSVC sc query "MSSQL$SQLEXPRESS" >nul 2>&1 && set "SQLSVC=MSSQL$SQLEXPRESS"

echo.
echo  GenZ-POS folder   : %APPDIR%
echo  Backup folder     : %BACKUP%
if defined SQLSVC (echo  SQL Server        : %SQLSVC%) else (echo  SQL Server        : NOT FOUND on this PC)
echo.

rem ---- 1. Backup folder + permissions ----------------------------------
echo [1/4] Creating the backup folder...
if not exist "%BACKUP%" mkdir "%BACKUP%"
if not exist "%BACKUP%" (
    echo       ERROR: could not create %BACKUP%
    goto :fail
)

echo [2/4] Giving SQL Server and Windows users permission to write backups...
rem *S-1-5-32-545 = the local "Users" group (works on any Windows language).
icacls "%BACKUP%" /grant "*S-1-5-32-545:(OI)(CI)M" >nul
if errorlevel 1 echo       WARNING: could not give "Users" access to %BACKUP%
if defined SQLSVC (
    icacls "%BACKUP%" /grant "NT Service\%SQLSVC%:(OI)(CI)M" >nul
    if errorlevel 1 echo       WARNING: could not give "NT Service\%SQLSVC%" access to %BACKUP%
)

rem ---- 2. SQL Server starts with Windows --------------------------------
echo [3/4] Making SQL Server start automatically with Windows...
if defined SQLSVC (
    sc config "%SQLSVC%" start= auto >nul
    sc start "%SQLSVC%" >nul 2>&1
) else (
    echo       SQL Server is not installed on this PC. If this is the MAIN computer, install
    echo       SQL Server Express first ^(see README-INSTALL.md^) and run setup.cmd again.
    echo       If this is a SECOND computer that uses the main computer's data, that's fine.
)

rem ---- 3. Desktop shortcut for everyone ---------------------------------
echo [4/4] Creating the "GenZ-POS" shortcut on the desktop...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d = [Environment]::GetFolderPath('CommonDesktopDirectory'); $s = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $d 'GenZ-POS.lnk')); $s.TargetPath = '%APPDIR%GenZ-POS.exe'; $s.WorkingDirectory = '%APPDIR%'; $s.IconLocation = '%APPDIR%GenZ-POS.exe,0'; $s.Description = 'GenZ-POS'; $s.Save()"
if errorlevel 1 echo       WARNING: could not create the desktop shortcut.

echo.
echo  Done.
echo.
echo  Optional (for a technician): invoice / record numbers can jump by about 1000 after
echo  SQL Server restarts. To stop that, open "SQL Server Configuration Manager" ^>
echo  SQL Server (MSSQLSERVER) ^> Properties ^> Startup Parameters, add  -T272  and restart it.
echo.
pause
exit /b 0

:fail
echo.
echo  Setup did not finish. Take a photo of this window and send it to your technician.
pause
exit /b 1
