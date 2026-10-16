# Compila o RafaelElectrical para cada Revit instalado e monta o RafaelElectrical.bundle.
# Uso:  .\build.ps1             -> compila e gera dist\RafaelElectrical.bundle
#       .\build.ps1 -Install    -> compila e instala em %APPDATA%\Autodesk\ApplicationPlugins
param(
    [string[]]$Versions = @('2025', '2026', '2027'),
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'src\RafaelElectrical.csproj'
$dist    = Join-Path $root 'dist'
$bundle  = Join-Path $dist 'RafaelElectrical.bundle'
$appId   = 'b1cd3190-7244-4d67-9363-32d3612206a1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'O .NET SDK não foi encontrado.' -ForegroundColor Red
    Write-Host 'Instale o .NET 8 SDK (Revit 2025/2026) e o .NET 10 SDK (Revit 2027): https://dotnet.microsoft.com/download' -ForegroundColor Yellow
    exit 1
}
$sdkMajors = (& dotnet --list-sdks) | ForEach-Object { [int]($_ -split '\.')[0] }
$maxSdk = ($sdkMajors | Measure-Object -Maximum).Maximum

# limpa saídas antigas (inclusive as que ficavam dentro de src)
foreach ($old in @('src\obj', 'src\bin', 'build')) {
    $p = Join-Path $root $old
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

$built = @()
foreach ($v in $Versions) {
    $revitDir = "C:\Program Files\Autodesk\Revit $v"
    if (-not (Test-Path "$revitDir\RevitAPI.dll")) {
        Write-Host "Revit $v não está instalado - pulando." -ForegroundColor DarkYellow
        continue
    }
    $needSdk = if ([int]$v -ge 2027) { 10 } else { 8 }
    if ($maxSdk -lt $needSdk) {
        Write-Host "Revit $v precisa do .NET $needSdk SDK (encontrado: $maxSdk) - pulando." -ForegroundColor DarkYellow
        continue
    }

    Write-Host "`n=== Compilando para Revit $v ===" -ForegroundColor Cyan
    $outDir = Join-Path $root "build\bin\$v"
    & dotnet build $proj -c Release "-p:RevitVersion=$v" -o $outDir -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Falha ao compilar para Revit $v." -ForegroundColor Red
        continue
    }

    $dest = Join-Path $bundle "Contents\$v"
    New-Item -ItemType Directory -Force $dest | Out-Null
    $dll = Join-Path $outDir 'RafaelElectrical.dll'
    if (-not (Test-Path $dll)) {
        Write-Host "DLL não encontrada em $outDir." -ForegroundColor Red
        continue
    }
    Copy-Item $dll $dest
    Copy-Item (Join-Path $root 'bundle\RafaelElectrical.addin') $dest
    $built += $v
}

if ($built.Count -eq 0) {
    Write-Host "`nNenhuma versão foi compilada." -ForegroundColor Red
    exit 1
}

# PackageContents.xml só com as versões compiladas
$components = ($built | ForEach-Object {
@"
  <Components Description="Revit $_">
    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R$_" SeriesMax="R$_" />
    <ComponentEntry AppName="RafaelElectrical" ModuleName=".\Contents\$_\RafaelElectrical.addin" />
  </Components>
"@
}) -join "`r`n"

$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Revit" Name="RafaelElectrical" AppVersion="1.0.0" ProductType="Application" ProductCode="$appId">
  <CompanyDetails Name="Rafael Matias" />
$components
</ApplicationPackage>
"@
Set-Content -Path (Join-Path $bundle 'PackageContents.xml') -Value $xml -Encoding UTF8

$zip = Join-Path $dist 'RafaelElectrical.bundle.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $bundle -DestinationPath $zip

Write-Host "`nBundle gerado: $bundle" -ForegroundColor Green
Write-Host "Versões: $($built -join ', ')" -ForegroundColor Green

if ($Install) {
    $plugins = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
    $target  = Join-Path $plugins 'RafaelElectrical.bundle'
    New-Item -ItemType Directory -Force $plugins | Out-Null
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $bundle $plugins -Recurse
    Write-Host "Instalado em: $target" -ForegroundColor Green
    Write-Host 'Abra (ou reinicie) o Revit para ver a aba RafaelElectrical.' -ForegroundColor Green
}
