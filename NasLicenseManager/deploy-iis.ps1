# Deploys the License Manager to IIS at http://localhost:5300. Run as administrator.
# Re-running it updates the files and keeps the site, pool, SQL login and key folder as they are.
#   powershell -ExecutionPolicy Bypass -File D:\NasLicenseManager\deploy-iis.ps1
#
# The pool runs as its own identity (IIS AppPool\<PoolName>), no password needed. web.config points the app at a
# machine-encrypted signing key in $KeyFolder; this script limits that folder to the pool, SYSTEM and Administrators.
param(
    [string]$SiteName = "NasLicenseManager",
    [string]$PoolName = "NasLicMgr",
    [string]$SitePath = "C:\inetpub\NasLicenseManager",
    [string]$KeyFolder = "C:\ProgramData\NasLicenseManager",
    [string]$SqlServer = "localhost\MSSQLSERVER2025",
    [string]$Database = "NasLicensing",
    [int]$Port = 5300
)
$ErrorActionPreference = "Stop"
Start-Transcript -Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "deploy-iis.log") -Force | Out-Null
trap { Write-Host "FAILED: $_" -ForegroundColor Red; Stop-Transcript | Out-Null; break }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script as administrator." }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root "publish"
$poolAccount = "IIS AppPool\$PoolName"

Write-Host "== Publishing" -ForegroundColor Cyan
dotnet publish (Join-Path $root "NasLicenseManager\NasLicenseManager.csproj") -c Release -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Port in use by something other than IIS (e.g. a 'dotnet run' copy)?
$owners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique | Where-Object { $_ -ne 4 }
foreach ($procId in $owners) {
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if (-not $p) { continue }
    $answer = Read-Host "Port $Port is used by $($p.ProcessName) (PID $procId, $($p.Path)). Stop it? [y/N]"
    if ($answer -match '^[yY]') { Stop-Process -Id $procId -Force; Start-Sleep -Seconds 1 }
    else { throw "Port $Port is busy. Stop that program or re-run with -Port <other>." }
}

Import-Module WebAdministration

Write-Host "== Application pool $PoolName (runs as $poolAccount)" -ForegroundColor Cyan
if (-not (Test-Path "IIS:\AppPools\$PoolName")) { New-WebAppPool $PoolName | Out-Null }
Set-ItemProperty "IIS:\AppPools\$PoolName" managedRuntimeVersion ""
Set-ItemProperty "IIS:\AppPools\$PoolName" processModel -Value @{ identityType = 4; userName = ""; password = ""; loadUserProfile = $true }
# Keep it running so the first request after idle isn't slow.
Set-ItemProperty "IIS:\AppPools\$PoolName" processModel.idleTimeout ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$PoolName" startMode "AlwaysRunning"

Write-Host "== Copying files to $SitePath" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $SitePath | Out-Null
$offline = Join-Path $SitePath "app_offline.htm"
if (Test-Path (Join-Path $SitePath "web.config")) {
    Set-Content $offline "<h1>Updating the License Manager, try again in a minute.</h1>" -Encoding ascii
    Start-Sleep -Seconds 3
}
robocopy $publish $SitePath /MIR /XF app_offline.htm /XD logs /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy exit $LASTEXITCODE)." }
$logs = Join-Path $SitePath "logs"
New-Item -ItemType Directory -Force $logs | Out-Null
icacls $logs /grant "${poolAccount}:(OI)(CI)M" | Out-Null

Write-Host "== Signing key folder $KeyFolder" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $KeyFolder | Out-Null
# Only Administrators, SYSTEM and the pool. The key is encrypted for the machine, so these permissions are what protect it.
icacls $KeyFolder /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" "${poolAccount}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Setting permissions on $KeyFolder failed." }
if (-not (Test-Path (Join-Path $KeyFolder "signing-key.bin"))) {
    Write-Host "No signing key in $KeyFolder yet: restore the backup on the Signing key page after signing in." -ForegroundColor Yellow
}

Write-Host "== SQL Server login for $poolAccount on $SqlServer/$Database" -ForegroundColor Cyan
$sql = @"
IF SUSER_ID(N'$poolAccount') IS NULL CREATE LOGIN [$poolAccount] FROM WINDOWS;
IF DB_ID(N'$Database') IS NOT NULL
BEGIN
    EXEC(N'USE [$Database];
        IF USER_ID(N''$poolAccount'') IS NULL CREATE USER [$poolAccount] FOR LOGIN [$poolAccount];
        ALTER ROLE db_owner ADD MEMBER [$poolAccount];');
END
ELSE
    -- First start creates the database, which needs this right.
    GRANT CREATE ANY DATABASE TO [$poolAccount];
"@
sqlcmd -S $SqlServer -E -C -b -Q $sql
if ($LASTEXITCODE -ne 0) { throw "Granting SQL Server access failed." }

Write-Host "== Website $SiteName on http://localhost:$Port" -ForegroundColor Cyan
if (-not (Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -Port $Port -HostHeader "localhost" -PhysicalPath $SitePath -ApplicationPool $PoolName | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" physicalPath $SitePath
    Set-ItemProperty "IIS:\Sites\$SiteName" applicationPool $PoolName
}

Remove-Item $offline -ErrorAction SilentlyContinue
if ((Get-WebAppPoolState $PoolName).Value -eq "Started") { Restart-WebAppPool $PoolName }
else { Start-WebAppPool $PoolName }
if ((Get-WebsiteState $SiteName).Value -ne "Started") { Start-Website $SiteName }

Write-Host "== Checking http://localhost:$Port" -ForegroundColor Cyan
try {
    $r = Invoke-WebRequest "http://localhost:$Port/" -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 60
    Write-Host "OK: HTTP $($r.StatusCode). The License Manager is running in IIS." -ForegroundColor Green
    Start-Process "http://localhost:$Port"
} catch {
    Write-Host "The site did not answer correctly: $($_.Exception.Message)" -ForegroundColor Red
    $since = (Get-Date).AddMinutes(-5)
    Get-WinEvent -LogName Application, System -MaxEvents 200 |
        Where-Object { $_.ProviderName -match 'AspNetCore|WAS|W3SVC' -and $_.TimeCreated -gt $since } |
        Select-Object -First 4 | ForEach-Object { Write-Host "[$($_.ProviderName)] $($_.Message)" }
    Write-Host "For more detail set stdoutLogEnabled=""true"" in $SitePath\web.config and read $logs." -ForegroundColor Yellow
}
Stop-Transcript | Out-Null
