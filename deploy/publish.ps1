# Builds the shop-ready GenZ-POS folder: ..\dist\GenZ-POS (self-contained, no .NET install needed on the shop PC).
# Usage (from any folder):  powershell -ExecutionPolicy Bypass -File deploy\publish.ps1
#Requires -Version 5.1
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\dist\GenZ-POS')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\GenZ-POS\GenZ-POS.csproj'
$Output = [IO.Path]::GetFullPath($Output)

# Start clean so files removed from the app don't linger in the published folder.
if (Test-Path -LiteralPath $Output) {
    Write-Host "Cleaning $Output"
    Remove-Item -LiteralPath $Output -Recurse -Force
}

Write-Host "Publishing to $Output ..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

# Post-checks: the things that silently break a Blazor Hybrid app when missing.
$required = @(
    'GenZ-POS.exe',
    'appsettings.json',
    'WebView2Loader.dll',
    'wwwroot\index.html',
    'wwwroot\GenZ-POS.styles.css',
    'wwwroot\_framework\blazor.webview.js',
    'wwwroot\_content\Radzen.Blazor\css\material.css',
    'wwwroot\_content\Radzen.Blazor\Radzen.Blazor.js'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Output $_)) })
if ($missing.Count -gt 0) {
    Write-Host 'PUBLISH CHECK FAILED - missing files:' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

# Ship the install / restore helpers with the app.
foreach ($f in 'setup.cmd', 'restore.cmd', 'restore.sql', 'README-INSTALL.md') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination $Output -Force
}

$version = (Get-Item -LiteralPath (Join-Path $Output 'GenZ-POS.exe')).VersionInfo.ProductVersion
$sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ''
Write-Host "OK  GenZ-POS $version published to $Output ($sizeMb MB)" -ForegroundColor Green
Write-Host 'Copy that whole folder to C:\GenZ-POS on the shop PC (see README-INSTALL.md).'
Write-Host 'When UPDATING a shop PC, do not overwrite its appsettings.json.'
