<#
  Builds installer\dist\KshethraSetup.exe - a single self-extracting setup for Kshethra Mithra on IIS.

    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-IncludeSqlExpress] [-SkipWebBuild]

  Contents: API (Release, framework-dependent, win-x64) with the Angular build in wwwroot, install/uninstall scripts,
  the .NET 9 Hosting Bundle, and optionally SQL Server 2022 Express media (-IncludeSqlExpress, ~250 MB) for offline PCs.
  Without -IncludeSqlExpress the target PC downloads SQL Express only if it has no SQL Server.
#>
[CmdletBinding()]
param([switch]$IncludeSqlExpress, [switch]$SkipWebBuild)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$inst = $PSScriptRoot
$stage = Join-Path $inst 'obj\stage'
$cache = Join-Path $inst 'cache'
$dist = Join-Path $inst 'dist'

function Step([string]$t) { Write-Host "==> $t" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$argv, [string]$cwd) {
    Push-Location $cwd
    try { & $exe @argv; if ($LASTEXITCODE -ne 0) { throw "$exe $($argv -join ' ') failed with exit code $LASTEXITCODE" } } finally { Pop-Location }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage, $cache, $dist, (Join-Path $stage 'prereqs') | Out-Null

Step "Publishing the API (Release)"
Run 'dotnet' @('publish', 'Kshethra.Api\Kshethra.Api.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
              '-o', (Join-Path $stage 'app'), '/p:DebugType=None', '/p:DebugSymbols=false', '--nologo', '-v', 'minimal') (Join-Path $root 'src\api')
Remove-Item (Join-Path $stage 'app\appsettings.Development.json') -ErrorAction SilentlyContinue

Step "Building the web app"
$web = Join-Path $root 'src\web'
if (-not $SkipWebBuild) {
    if (-not (Test-Path (Join-Path $web 'node_modules'))) { Run 'npm.cmd' @('ci') $web }
    Run 'npx.cmd' @('ng', 'build', '--configuration', 'production') $web
}
$browser = Join-Path $web 'dist\kshethra-web\browser'
if (-not (Test-Path (Join-Path $browser 'index.html'))) { throw "Angular build output not found at $browser" }
Copy-Item $browser (Join-Path $stage 'app\wwwroot') -Recurse

Step "Prerequisites"
$bundle = Join-Path $cache 'dotnet-hosting-win.exe'
if (-not (Test-Path $bundle)) {
    Write-Host "    Downloading .NET 9 Hosting Bundle..."
    Invoke-WebRequest 'https://aka.ms/dotnet/9.0/dotnet-hosting-win.exe' -OutFile $bundle -UseBasicParsing
}
Copy-Item $bundle (Join-Path $stage 'prereqs')
if ($IncludeSqlExpress) {
    $media = Get-ChildItem $cache -Filter 'SQLEXPR*_x64_*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $media) {
        Write-Host "    Downloading SQL Server 2022 Express media..."
        $ssei = Join-Path $cache 'SQL2022-SSEI-Expr.exe'
        if (-not (Test-Path $ssei)) { Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?linkid=2216019' -OutFile $ssei -UseBasicParsing }
        Start-Process $ssei -ArgumentList '/ACTION=Download', "/MEDIAPATH=$cache", '/MEDIATYPE=Core', '/QUIET' -Wait
        $media = Get-ChildItem $cache -Filter 'SQLEXPR*_x64_*.exe' -Recurse | Select-Object -First 1
        if (-not $media) { throw "SQL Server Express media download failed." }
    }
    Copy-Item $media.FullName (Join-Path $stage 'prereqs\SQLEXPR_x64_ENU.exe')
}
Copy-Item (Join-Path $inst 'payload\install.ps1'), (Join-Path $inst 'payload\uninstall.ps1') $stage

Step "Compressing payload"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = Join-Path $inst 'obj\payload.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
# Written entry by entry: ZipFile.CreateFromDirectory under Windows PowerShell stores '\' separators, which is not standard zip.
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
        $name = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $level = if ($f.Extension -eq '.exe' -and $name -like 'prereqs/*') { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, $level)
    }
} finally { $archive.Dispose() }

Step "Building the setup launcher"
$launcherOut = Join-Path $inst 'obj\launcher'
Run 'dotnet' @('build', 'KshethraSetup\KshethraSetup.csproj', '-c', 'Release', '-o', $launcherOut, '--nologo', '-v', 'minimal') $inst

Step "Writing KshethraSetup.exe"
$out = Join-Path $dist 'KshethraSetup.exe'
$fs = [IO.File]::Create($out)
try {
    foreach ($part in (Join-Path $launcherOut 'KshethraSetup.exe'), $zip) {
        $in = [IO.File]::OpenRead($part); try { $in.CopyTo($fs) } finally { $in.Dispose() }
    }
    $len = (Get-Item $zip).Length
    $fs.Write([BitConverter]::GetBytes([long]$len), 0, 8)
    $magic = [Text.Encoding]::ASCII.GetBytes('KSHMIT01')
    $fs.Write($magic, 0, 8)
} finally { $fs.Dispose() }

$mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host ""
Write-Host "Setup ready: $out ($mb MB)" -ForegroundColor Green
Write-Host "Run it as Administrator on the target PC; the app opens at http://localhost:4400"
