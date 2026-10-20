# Compila o RafaelElectrical para cada Revit instalado e monta o RafaelElectrical.bundle.
# Uso:  .\build.ps1             -> compila e gera dist\RafaelElectrical.bundle
#       .\build.ps1 -Install    -> compila e instala em %APPDATA%\Autodesk\ApplicationPlugins
param([switch]$Install)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$proj    = Join-Path $root 'src\RafaelElectrical.csproj'
$dist    = Join-Path $root 'dist'
$bundle  = Join-Path $dist 'RafaelElectrical.bundle'
$appId   = 'b1cd3190-7244-4d67-9363-32d3612206a1'
$Versions = @('2025', '2026', '2027')

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'O .NET SDK não foi encontrado.' -ForegroundColor Red
    Write-Host 'Instale o .NET 10 SDK: https://dotnet.microsoft.com/download' -ForegroundColor Yellow
    exit 1
}
$sdkMajors = (& dotnet --list-sdks) | ForEach-Object { [int]($_ -split '\.')[0] }
$maxSdk = ($sdkMajors | Measure-Object -Maximum).Maximum

if ($maxSdk -lt 10) { throw 'É necessário o .NET 10 SDK para compilar o pacote 2025–2027.' }

# limpa saídas antigas (inclusive as que ficavam dentro de src)
$rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
foreach ($old in @('src\obj', 'src\bin', 'build')) {
    $p = Join-Path $root $old
    if (-not [IO.Path]::GetFullPath($p).StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Caminho de limpeza inválido: $p" }
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}
if (-not [IO.Path]::GetFullPath($bundle).StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Caminho de limpeza inválido: $bundle" }
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
$zip = Join-Path $dist 'RafaelElectrical.bundle.zip'
if (-not [IO.Path]::GetFullPath($zip).StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Caminho de limpeza inválido: $zip" }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

$built = @()
foreach ($v in $Versions) {
    Write-Host "`n=== Compilando para Revit $v ===" -ForegroundColor Cyan
    $outDir = Join-Path $root "build\bin\$v"
    & dotnet build $proj -c Release "-p:RevitVersion=$v" -o $outDir -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao compilar para Revit $v. Pacote 2025–2027 não foi gerado."
    }

    $dest = Join-Path $bundle "Contents\$v"
    New-Item -ItemType Directory -Force $dest | Out-Null
    $dll = Join-Path $outDir 'RafaelElectrical.dll'
    if (-not (Test-Path $dll)) {
        throw "DLL não encontrada em $outDir. Pacote 2025–2027 não foi gerado."
    }
    Copy-Item $dll $dest
    Copy-Item (Join-Path $root 'bundle\RafaelElectrical.addin') $dest
    $built += $v
}

if ($built.Count -ne 3) { throw "Pacote incompleto: $($built -join ', ')." }

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
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="Revit" Name="RafaelElectrical" AppVersion="0.2.1" ProductType="Application" ProductCode="$appId">
  <CompanyDetails Name="Rafael Matias" />
$components
</ApplicationPackage>
"@
Set-Content -Path (Join-Path $bundle 'PackageContents.xml') -Value $xml -Encoding UTF8

Copy-Item (Join-Path $root 'installer\Instalar.ps1') $dist -Force
Copy-Item (Join-Path $root 'installer\LEIA-ME.txt') $dist -Force
Compress-Archive -Path $bundle, (Join-Path $dist 'Instalar.ps1'), (Join-Path $dist 'LEIA-ME.txt') -DestinationPath $zip

Write-Host "`nBundle gerado: $bundle" -ForegroundColor Green
Write-Host "Versões: $($built -join ', ')" -ForegroundColor Green

if ($Install) { & (Join-Path $dist 'Instalar.ps1') }
