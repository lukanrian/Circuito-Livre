$ErrorActionPreference = 'Stop'
if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { throw 'Feche o Revit antes de instalar.' }
$pluginFolder = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$pluginTarget = Join-Path $pluginFolder 'CircuitoLivre.bundle'
$pluginSource = Join-Path $PSScriptRoot 'CircuitoLivre.bundle'
if (-not (Test-Path -LiteralPath (Join-Path $pluginSource 'PackageContents.xml'))) { throw 'Extraia todo o ZIP antes de instalar.' }
foreach ($version in @('2025', '2026', '2027')) {
    if (-not (Test-Path -LiteralPath (Join-Path $pluginSource "Contents\$version\CircuitoLivre.dll"))) {
        throw "Pacote incompleto: falta a DLL do Revit $version."
    }
}
New-Item -ItemType Directory -Force -Path $pluginFolder | Out-Null
if (Test-Path -LiteralPath $pluginTarget) {
    $backupTarget = Join-Path $PSScriptRoot ('Backup-CircuitoLivre-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    Copy-Item -LiteralPath $pluginTarget -Destination $backupTarget -Recurse
    Write-Host "Backup salvo em $backupTarget"
}
New-Item -ItemType Directory -Force -Path $pluginTarget | Out-Null
Get-ChildItem -LiteralPath $pluginSource | Copy-Item -Destination $pluginTarget -Recurse -Force
Write-Host "Instalado para Revit 2025, 2026 e 2027 em $pluginTarget. Abra o Revit e recalcule o projeto inteiro."
