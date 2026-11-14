<#
  NAS POS Pro - IIS installer.
  Runs from the extracted setup folder (NasPosSetup.exe does this, elevated). Safe to re-run: an existing install is upgraded in place
  (settings and JWT key are kept, migrations are applied, the database is never dropped).

  Steps: IIS features -> ASP.NET Core Hosting Bundle -> SQL Server (use a local instance, or install SQL Express)
         -> SQL login + database -> copy files -> settings -> IIS app pool + site on :Port -> database migrations (all tables)
         -> firewall, shortcut.

  Passwords can be passed in the environment instead of the command line (NasPosSetup.exe does this):
    NASSETUP_SQL_PASSWORD  - password of -SqlUser (admin login used by setup)
    NASSETUP_APP_PASSWORD  - password of -AppSqlUser (login the application connects with)
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:SystemDrive\inetpub\NasPosPro",
    [int]$Port = 4300,
    [string]$SqlInstance = "",          # e.g. "localhost", "localhost\SQLEXPRESS". Empty = auto-detect.
    [string]$DbName = "NASDB",
    [string]$SqlUser = "",              # optional SQL admin login for install (default: Windows login of the installer)
    [string]$SqlPassword = "",
    [string]$AppSqlUser = "NAS",        # SQL login created for the application (db_owner of $DbName only)
    [string]$AppSqlPassword = "",       # default admin@123
    [string]$BackupInterval = "",       # 1h|2h|4h|6h|12h|Daily|Weekly|Off. Empty = leave the backup schedule as it is.
    [string]$BackupDir = "",            # default: <second fixed disk or system disk>\NASBackup
    [string]$BackupTime = "21:00",      # HH:mm - time of day (Daily/Weekly) or start time (hourly)
    [string]$BackupDay = "SUN",         # Weekly only: MON..SUN
    [int]$BackupKeepDays = 30,
    [switch]$InstallSqlExpress,         # install a new SQL Server Express instance even if others exist
    [switch]$NoSqlInstall,              # fail instead of installing SQL Express when no instance exists
    [switch]$NoFirewall,
    [switch]$NoBrowser,
    [switch]$Gui,                       # emit ##PROGRESS / ##ERROR markers for NasPosSetup.exe
    [switch]$Quiet                      # no prompts; take defaults
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$SiteName = 'NAS POS Pro'
$PoolName = 'NasPosPro'
$PoolIdentity = "IIS APPPOOL\$PoolName"
$DataDir = Join-Path $env:ProgramData 'NasPosPro'
$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

if (-not $SqlPassword -and $env:NASSETUP_SQL_PASSWORD) { $SqlPassword = $env:NASSETUP_SQL_PASSWORD }
if (-not $AppSqlPassword) { $AppSqlPassword = if ($env:NASSETUP_APP_PASSWORD) { $env:NASSETUP_APP_PASSWORD } else { 'admin@123' } }
Remove-Item Env:NASSETUP_SQL_PASSWORD, Env:NASSETUP_APP_PASSWORD -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$prevState = $null
try { if (Test-Path (Join-Path $DataDir 'install.json')) { $prevState = Get-Content (Join-Path $DataDir 'install.json') -Raw | ConvertFrom-Json } } catch { }
$log =Join-Path $DataDir ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $log -Force | Out-Null

function Step([string]$text, [int]$pct) {
    Write-Host ""
    if ($Gui) { Write-Host "##PROGRESS|$pct|$text" }
    Write-Host "==> $text" -ForegroundColor Cyan
}
function Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Info([string]$text) { Write-Host "    $text" }
function Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }
function Fail([string]$text) { throw $text }
function Ask([string]$question, [string]$default) {
    if ($Quiet) { return $default }
    $a = Read-Host "    $question [$default]"
    if ([string]::IsNullOrWhiteSpace($a)) { return $default } else { return $a.Trim() }
}

function New-Cs([string]$database, [string]$user, [string]$password, [string]$appName = '') {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $SqlInstance
    $b['Initial Catalog'] = $database
    if ($user) { $b['User ID'] = $user; $b['Password'] = $password } else { $b['Integrated Security'] = $true }
    $b['TrustServerCertificate'] = $true
    $b['Connect Timeout'] = 15
    if ($appName) { $b['Application Name'] = $appName }
    return $b.ConnectionString
}

function Invoke-Sql([string]$connectionString, [string]$sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
        return $cmd.ExecuteScalar()
    } finally { $cn.Dispose() }
}

try {
    # ------------------------------------------------------------------ 0. checks
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Fail "Run the setup as Administrator."
    }
    if (-not [Environment]::Is64BitOperatingSystem) { Fail "A 64-bit Windows is required." }
    if (-not (Test-Path (Join-Path $here 'app\NasPos.Api.dll'))) { Fail "Setup files are incomplete (app\NasPos.Api.dll missing)." }
    if ($DbName -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,99}$') { Fail "Database name '$DbName' is not valid (letters, digits and _ only)." }
    if ($AppSqlUser -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,99}$' -or $AppSqlUser -in 'sa', 'dbo', 'guest') { Fail "SQL login name '$AppSqlUser' is not valid." }
    if ($BackupInterval -and $BackupInterval -notin '1h', '2h', '4h', '6h', '12h', 'Daily', 'Weekly', 'Off') { Fail "Backup interval '$BackupInterval' is not valid (1h, 2h, 4h, 6h, 12h, Daily, Weekly or Off)." }
    if ($BackupTime -notmatch '^([01]\d|2[0-3]):[0-5]\d$') { Fail "Backup time '$BackupTime' is not valid (HH:mm)." }
    if ($BackupDay -notin 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT', 'SUN') { Fail "Backup day '$BackupDay' is not valid (MON..SUN)." }
    if ($BackupKeepDays -lt 1 -or $BackupKeepDays -gt 3650) { Fail "Backup retention must be 1 to 3650 days." }
    if (-not $BackupDir) {
        $sysRoot = [IO.Path]::GetPathRoot($env:SystemRoot)
        $other = [IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady -and $_.Name -ne $sysRoot } | Select-Object -First 1
        $BackupDir = Join-Path $(if ($other) { $other.Name } else { $sysRoot }) 'NASBackup'
    }
    $BackupDir = $BackupDir.TrimEnd('\'); if ($BackupDir -match '^[A-Za-z]:$') { $BackupDir += '\' }

    Write-Host ""
    Write-Host "  NAS POS Pro - Setup" -ForegroundColor White
    Write-Host "  Install folder : $InstallDir"
    Write-Host "  Web address    : http://localhost:$Port"
    Write-Host "  Database       : $DbName (login $AppSqlUser)"
    Write-Host "  Log            : $log"
    if ($Gui) { Write-Host "##LOG|$log" }

    # ------------------------------------------------------------------ 1. IIS
    Step "Enabling IIS (Internet Information Services)" 3
    $features = 'IIS-WebServerRole', 'IIS-WebServer', 'IIS-CommonHttpFeatures', 'IIS-StaticContent', 'IIS-DefaultDocument', 'IIS-HttpErrors',
                'IIS-HealthAndDiagnostics', 'IIS-HttpLogging', 'IIS-Security', 'IIS-RequestFiltering', 'IIS-Performance', 'IIS-HttpCompressionStatic',
                'IIS-WebSockets', 'IIS-ApplicationInit', 'IIS-ManagementConsole'
    $missing = @($features | Where-Object { (Get-WindowsOptionalFeature -Online -FeatureName $_ -ErrorAction SilentlyContinue).State -ne 'Enabled' })
    $restartNeeded = $false
    if ($missing.Count -gt 0) {
        Info ("Turning on: " + ($missing -join ', '))
        $r = Enable-WindowsOptionalFeature -Online -FeatureName $missing -All -NoRestart
        if ($r.RestartNeeded) { $restartNeeded = $true }
        Ok "IIS features enabled."
    } else { Ok "IIS is already enabled." }
    if (-not (Test-Path $appcmd)) { Fail "IIS did not install correctly (appcmd.exe not found). Restart Windows and run the setup again." }
    Set-Service W3SVC -StartupType Automatic; Start-Service W3SVC

    # ------------------------------------------------------------------ 2. ASP.NET Core Hosting Bundle
    Step "Checking the .NET 9 Hosting Bundle (ASP.NET Core Module for IIS)" 15
    function Test-Ancm { & $appcmd list module /name:AspNetCoreModuleV2 *> $null; return $LASTEXITCODE -eq 0 }
    function Test-Runtime9 {
        $d = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.AspNetCore.App'
        return (Test-Path $d) -and @(Get-ChildItem $d -Directory | Where-Object Name -like '9.*').Count -gt 0
    }
    if (-not ((Test-Ancm) -and (Test-Runtime9))) {
        $bundle = Join-Path $here 'prereqs\dotnet-hosting-win.exe'
        if (-not (Test-Path $bundle)) {
            Info "Downloading the Hosting Bundle from Microsoft..."
            $bundle = Join-Path $env:TEMP 'dotnet-hosting-win.exe'
            Invoke-WebRequest 'https://aka.ms/dotnet/9.0/dotnet-hosting-win.exe' -OutFile $bundle -UseBasicParsing
        }
        Info "Installing (this can take a few minutes)..."
        $p = Start-Process $bundle -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
        if ($p.ExitCode -eq 3010) { $restartNeeded = $true } elseif ($p.ExitCode -notin 0, 1638) { Fail "Hosting Bundle install failed (exit code $($p.ExitCode))." }
        if (-not (Test-Ancm)) {
            # Bundle was installed before IIS existed: a repair registers the IIS module.
            Info "Registering the module with IIS..."
            $p = Start-Process $bundle -ArgumentList '/repair', '/quiet', '/norestart' -Wait -PassThru
            if ($p.ExitCode -eq 3010) { $restartNeeded = $true }
        }
        if (-not (Test-Ancm)) { Fail "The ASP.NET Core Module is not registered in IIS. Restart Windows and run the setup again." }
        & net stop was /y | Out-Null
        & net start w3svc | Out-Null
        Ok "Hosting Bundle installed."
    } else { Ok "Already installed." }
    if (-not (Test-Path $dotnet)) { Fail "dotnet.exe not found at $dotnet after installing the Hosting Bundle." }

    # ------------------------------------------------------------------ 3. SQL Server
    Step "Finding SQL Server" 25
    function Get-SqlInstances {
        $key = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
        if (-not (Test-Path $key)) { return @() }
        $props = (Get-ItemProperty $key).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }
        foreach ($p in $props) {
            $svc = if ($p.Name -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { 'MSSQL$' + $p.Name }
            $server = if ($p.Name -eq 'MSSQLSERVER') { 'localhost' } else { "localhost\$($p.Name)" }
            $s = Get-Service $svc -ErrorAction SilentlyContinue
            [pscustomobject]@{ Name = $p.Name; Server = $server; Service = $svc; Status = if ($s) { "$($s.Status)" } else { 'Unknown' } }
        }
    }
    function Install-SqlExpress([string]$instanceName) {
        Step "Installing SQL Server 2022 Express (instance $instanceName)" 28
        $media = Join-Path $here 'prereqs\SQLEXPR_x64_ENU.exe'
        $work = Join-Path $env:TEMP 'NasPosSql'
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        if (-not (Test-Path $media)) {
            Info "Downloading SQL Server 2022 Express from Microsoft (about 265 MB)..."
            $media = Join-Path $work 'SQLEXPR_x64_ENU.exe'
            Invoke-WebRequest 'https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SQLEXPR_x64_ENU.exe' -OutFile $media -UseBasicParsing
            $sig = Get-AuthenticodeSignature $media
            if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation*') { Fail "The downloaded SQL Server Express file is not signed by Microsoft." }
        }
        Info "Extracting..."
        $extract = Join-Path $work 'setup'
        Start-Process $media -ArgumentList '/Q', "/X:$extract" -Wait | Out-Null
        Info "Installing (this can take 5-15 minutes)..."
        # Mixed mode is switched on below for every instance (new or existing), so the new instance starts Windows-only like any other.
        $sqlArgs = '/Q', '/ACTION=Install', '/FEATURES=SQLENGINE', "/INSTANCENAME=$instanceName", '/SQLSYSADMINACCOUNTS="BUILTIN\Administrators"',
                   '/TCPENABLED=0', '/UPDATEENABLED=0', '/IACCEPTSQLSERVERLICENSETERMS'
        $p = Start-Process (Join-Path $extract 'setup.exe') -ArgumentList $sqlArgs -Wait -PassThru
        if ($p.ExitCode -eq 3010) { $script:restartNeeded = $true } elseif ($p.ExitCode -ne 0) { Fail "SQL Server Express install failed (exit code $($p.ExitCode)). See %ProgramFiles%\Microsoft SQL Server\*\Setup Bootstrap\Log." }
        Ok "SQL Server Express installed."
    }
    function Restart-SqlService([string]$service) {
        $deps = @(Get-Service $service -DependentServices | Where-Object Status -eq 'Running')
        Restart-Service $service -Force
        $deps | ForEach-Object { Start-Service $_.Name -ErrorAction SilentlyContinue }
    }

    $instances = @(Get-SqlInstances)
    if ($InstallSqlExpress -or ([string]::IsNullOrWhiteSpace($SqlInstance) -and $instances.Count -eq 0)) {
        if ($NoSqlInstall) { Fail "No SQL Server instance found on this computer." }
        $newName = if ($instances | Where-Object Name -eq 'SQLEXPRESS') { 'NASSQL' } else { 'SQLEXPRESS' }
        if ($instances | Where-Object Name -eq $newName) { Fail "SQL Server instances SQLEXPRESS and NASSQL already exist. Choose one of them instead of installing a new one." }
        Install-SqlExpress $newName
        $instances = @(Get-SqlInstances)
        $SqlInstance = "localhost\$newName"
        $SqlUser = ''; $SqlPassword = ''    # setup runs as an Administrator, which is sysadmin on the new instance
    } elseif ([string]::IsNullOrWhiteSpace($SqlInstance)) {
        $running = @($instances | Where-Object Status -eq 'Running')
        $default = if ($running.Count -gt 0) { $running[0].Server } else { $instances[0].Server }
        Info "Found:"
        $instances | ForEach-Object { Info ("  {0,-32} {1}" -f $_.Server, $_.Status) }
        $SqlInstance = Ask "SQL Server to use" $default
    }
    # Make sure the chosen instance's service is running and starts with Windows.
    $chosen = $instances | Where-Object { $_.Server -ieq $SqlInstance -or ".\$($_.Name)" -ieq $SqlInstance -or ($_.Name -eq 'MSSQLSERVER' -and $SqlInstance -in '.', '(local)', 'localhost') } | Select-Object -First 1
    if ($chosen) {
        Set-Service $chosen.Service -StartupType Automatic
        if ((Get-Service $chosen.Service).Status -ne 'Running') { Info "Starting $($chosen.Service)..."; Start-Service $chosen.Service }
    }

    $adminCs = New-Cs 'master' $SqlUser $SqlPassword
    $isAdmin = $null
    for ($try = 0; $try -lt 3 -and $null -eq $isAdmin; $try++) {
        try {
            $isAdmin = Invoke-Sql $adminCs "SELECT IS_SRVROLEMEMBER('sysadmin')"
        } catch {
            Warn "Could not connect to $SqlInstance : $($_.Exception.Message)"
            if ($Quiet) { throw }
            $SqlUser = Ask "SQL admin login (e.g. sa)" "sa"
            $sec = Read-Host "    Password for $SqlUser" -AsSecureString
            $SqlPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
            $adminCs = New-Cs 'master' $SqlUser $SqlPassword
        }
    }
    if ($null -eq $isAdmin) { Fail "Could not connect to SQL Server $SqlInstance." }
    if ($isAdmin -ne 1) { Fail "The login used by setup is not a sysadmin on $SqlInstance. Sign in with an administrator login (e.g. sa)." }
    Ok "Connected to $SqlInstance as administrator."

    # The application signs in with a SQL login, so the instance must accept SQL Server authentication (mixed mode).
    $windowsOnly = Invoke-Sql $adminCs "SELECT CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly'))"
    if ($windowsOnly -eq 1) {
        if (-not $chosen) { Fail "$SqlInstance accepts Windows logins only. Turn on 'SQL Server and Windows Authentication mode' in SSMS (Server properties > Security), restart it and run setup again." }
        Info "Turning on SQL Server and Windows Authentication mode (restarts $($chosen.Service))..."
        Invoke-Sql $adminCs "EXEC master.dbo.xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer', N'LoginMode', REG_DWORD, 2; SELECT 1;" | Out-Null
        Restart-SqlService $chosen.Service
        $back = $false
        for ($i = 0; $i -lt 30 -and -not $back; $i++) {
            try { $windowsOnly = Invoke-Sql $adminCs "SELECT CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly'))"; $back = $true } catch { Start-Sleep -Seconds 2 }
        }
        if (-not $back) { Fail "SQL Server $SqlInstance did not come back after the restart." }
        if ($windowsOnly -eq 1) { Fail "Could not switch $SqlInstance to mixed authentication mode." }
        Ok "Mixed authentication mode is on."
    }

    # ------------------------------------------------------------------ 4. login + database
    Step "Creating database $DbName and SQL login $AppSqlUser" 40
    $pw = $AppSqlPassword.Replace("'", "''")
    $created = Invoke-Sql $adminCs "IF DB_ID(N'$DbName') IS NULL BEGIN CREATE DATABASE [$DbName]; SELECT 1; END ELSE SELECT 0;"
    if ($created -eq 1) { Ok "Database $DbName created." } else { Ok "Database $DbName already exists (kept, will be upgraded)." }
    # CHECK_POLICY OFF: the chosen password must work even where the Windows password policy is stricter; the login only reaches this database.
    Invoke-Sql $adminCs @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$AppSqlUser')
    CREATE LOGIN [$AppSqlUser] WITH PASSWORD = N'$pw', DEFAULT_DATABASE = [$DbName], CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ELSE
    ALTER LOGIN [$AppSqlUser] WITH PASSWORD = N'$pw', DEFAULT_DATABASE = [$DbName], CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ALTER LOGIN [$AppSqlUser] ENABLE;
SELECT 1;
"@ | Out-Null
    Invoke-Sql (New-Cs $DbName $SqlUser $SqlPassword) @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$AppSqlUser')
    CREATE USER [$AppSqlUser] FOR LOGIN [$AppSqlUser];
ELSE
    ALTER USER [$AppSqlUser] WITH LOGIN = [$AppSqlUser];
ALTER ROLE db_owner ADD MEMBER [$AppSqlUser];
SELECT 1;
"@ | Out-Null
    Ok "Login $AppSqlUser can sign in and owns $DbName (no server-wide rights)."
    $appCs = New-Cs $DbName $AppSqlUser $AppSqlPassword 'NAS POS Pro'
    try { Invoke-Sql $appCs "SELECT 1" | Out-Null } catch { Fail "Login $AppSqlUser could not sign in to $DbName : $($_.Exception.Message)" }

    # ------------------------------------------------------------------ 5. port check
    Step "Checking port $Port" 45
    $sites = & $appcmd list site 2>$null
    $clash = @($sites | Where-Object { $_ -match "[:*]$Port[:/]" -and $_ -notmatch [regex]::Escape("""$SiteName""") })
    if ($clash.Count -gt 0) { Fail "Port $Port is already used by another IIS site: $($clash -join '; '). Choose another port." }
    $listen = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -ne 4
    if ($listen) {
        $pn = (Get-Process -Id $listen[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
        Fail "Port $Port is in use by '$pn' (PID $($listen[0].OwningProcess)). Stop it or choose another port."
    }
    Ok "Port $Port is free."

    # ------------------------------------------------------------------ 6. IIS app pool (needed before files ACL)
    Step "Configuring the IIS application pool" 50
    & $appcmd list apppool /name:$PoolName *> $null
    if ($LASTEXITCODE -ne 0) { & $appcmd add apppool /name:$PoolName | Out-Null; Info "App pool '$PoolName' created." }
    else { & $appcmd stop apppool /apppool.name:$PoolName *> $null; Info "App pool '$PoolName' stopped for upgrade." }
    & $appcmd set apppool /apppool.name:$PoolName /managedRuntimeVersion:"" /managedPipelineMode:Integrated /startMode:AlwaysRunning `
        /processModel.identityType:ApplicationPoolIdentity /processModel.loadUserProfile:true /processModel.idleTimeout:00:00:00 `
        /recycling.periodicRestart.time:00:00:00 | Out-Null
    Start-Sleep -Seconds 2

    # ------------------------------------------------------------------ 7. files
    Step "Copying application files" 55
    New-Item -ItemType Directory -Force -Path $InstallDir, (Join-Path $InstallDir 'logs') | Out-Null
    & robocopy (Join-Path $here 'app') $InstallDir /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json /XD logs | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Copying files failed (robocopy exit code $LASTEXITCODE)." }
    Ok "Files copied to $InstallDir."

    # ------------------------------------------------------------------ 8. settings (JWT key is generated once and kept on upgrade)
    Step "Writing settings" 65
    $settingsPath = Join-Path $InstallDir 'appsettings.Production.json'
    $signingKey = $null
    if (Test-Path $settingsPath) {
        try { $signingKey = (Get-Content $settingsPath -Raw | ConvertFrom-Json).Jwt.SigningKey } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($signingKey) -or $signingKey.Length -lt 44) {
        $bytes = New-Object byte[] 48
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $signingKey = [Convert]::ToBase64String($bytes)
    }
    $settings = [ordered]@{
        ConnectionStrings = [ordered]@{ Default = $appCs }
        Jwt = [ordered]@{ SigningKey = $signingKey }
        Database = [ordered]@{ MigrateOnStartup = $false }
        # Plain HTTP on the shop LAN: no HTTPS redirect, refresh cookie without the Secure flag.
        Security = [ordered]@{ HttpsRedirect = $false; SecureCookies = $false }
    }
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    # The file holds the token signing key and the SQL password: only admins, SYSTEM and the app pool may read it.
    & icacls $settingsPath /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" "${PoolIdentity}:(R)" | Out-Null
    & icacls $InstallDir /grant "${PoolIdentity}:(OI)(CI)(RX)" | Out-Null
    & icacls (Join-Path $InstallDir 'logs') /grant "${PoolIdentity}:(OI)(CI)(M)" | Out-Null
    Ok "Settings saved (existing keys kept)."

    # ------------------------------------------------------------------ 9. tables
    Step "Creating / upgrading all tables in $DbName" 70
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:ConnectionStrings__Default = $appCs
    Push-Location $InstallDir
    try { & $dotnet (Join-Path $InstallDir 'NasPos.Api.dll') --migrate; $code = $LASTEXITCODE } finally { Pop-Location; Remove-Item Env:ConnectionStrings__Default }
    if ($code -ne 0) { Fail "Creating the tables failed (exit code $code). See the messages above." }
    $tables = Invoke-Sql $appCs "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0"
    Ok "Database is up to date ($tables tables)."

    # ------------------------------------------------------------------ 9b. automatic backup (failures here warn, they don't stop setup)
    $TaskName = 'NAS POS Pro Backup'
    if ($BackupInterval -eq '') {
        # Not asked (e.g. a /quiet upgrade): keep whatever schedule exists and remember its settings.
        if ($prevState -and $prevState.BackupInterval) {
            $BackupInterval = $prevState.BackupInterval; $BackupDir = $prevState.BackupDir; $BackupTime = $prevState.BackupTime
            $BackupDay = $prevState.BackupDay; $BackupKeepDays = $prevState.BackupKeepDays
        }
    } elseif ($BackupInterval -eq 'Off') {
        Step "Turning off automatic backup" 76
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Ok "Automatic backup is off."
    } else {
        Step "Setting up automatic backup to $BackupDir" 76
        try {
            if (-not $chosen) { throw "SQL Server $SqlInstance is not on this computer; set up backups on the SQL Server computer." }
            New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
            # SQL Server writes the .bak as its service account: give it the staging folder only; backup.ps1 (SYSTEM) moves the file.
            $stage = Join-Path $DataDir 'backup-staging'
            New-Item -ItemType Directory -Force -Path $stage | Out-Null
            $svcAccount = (Get-CimInstance Win32_Service -Filter "Name='$($chosen.Service)'").StartName
            if ($svcAccount -and $svcAccount -ne 'LocalSystem') {
                & icacls $stage /grant "${svcAccount}:(OI)(CI)(M)" | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Could not give $svcAccount access to $stage." }
            }
            Copy-Item (Join-Path $here 'backup.ps1') (Join-Path $DataDir 'backup.ps1') -Force
            $cfg = [ordered]@{ InstallDir = $InstallDir; BackupDir = $BackupDir; KeepDays = $BackupKeepDays }
            [IO.File]::WriteAllText((Join-Path $DataDir 'backup.json'), ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

            $at = [datetime]::ParseExact($BackupTime, 'HH:mm', $null)
            $days = @{ SUN = 'Sunday'; MON = 'Monday'; TUE = 'Tuesday'; WED = 'Wednesday'; THU = 'Thursday'; FRI = 'Friday'; SAT = 'Saturday' }
            switch ($BackupInterval) {
                'Daily'  { $trigger = New-ScheduledTaskTrigger -Daily -At $at; $when = "every day at $BackupTime" }
                'Weekly' { $trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek $days[$BackupDay] -At $at; $when = "every $($days[$BackupDay]) at $BackupTime" }
                default {
                    # Every N hours: a daily trigger that repeats for one day (works on every Windows 10/11 / Server build).
                    $hours = [int]$BackupInterval.TrimEnd('h')
                    $trigger = New-ScheduledTaskTrigger -Daily -At $at
                    $trigger.Repetition = (New-ScheduledTaskTrigger -Once -At $at -RepetitionInterval (New-TimeSpan -Hours $hours) -RepetitionDuration (New-TimeSpan -Days 1)).Repetition
                    $when = "every $hours hour(s) from $BackupTime"
                }
            }
            $action = New-ScheduledTaskAction -Execute "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" `
                -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $DataDir 'backup.ps1')`""
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            # StartWhenAvailable: a backup missed while the PC was off runs as soon as it is switched on.
            $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -ExecutionTimeLimit (New-TimeSpan -Hours 4) -MultipleInstances IgnoreNew
            Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings -Force `
                -Description "Backs up the $DbName database to $BackupDir ($when), keeps $BackupKeepDays days. Log: $DataDir\backup.log" | Out-Null
            Ok "Scheduled: $when, kept $BackupKeepDays days."

            Info "Making the first backup to check it works..."
            $before = (Get-ScheduledTaskInfo -TaskName $TaskName).LastRunTime
            Start-ScheduledTask -TaskName $TaskName
            $deadline = (Get-Date).AddMinutes(15)
            do {
                Start-Sleep -Seconds 2
                $info = Get-ScheduledTaskInfo -TaskName $TaskName
                $taskState = (Get-ScheduledTask -TaskName $TaskName).State
            } while ((Get-Date) -lt $deadline -and ($taskState -eq 'Running' -or $info.LastRunTime -eq $before))
            $last = Get-Content (Join-Path $DataDir 'backup.log') -Tail 1 -ErrorAction SilentlyContinue
            if ($taskState -eq 'Running') { Warn "The first backup is still running; check $DataDir\backup.log later." }
            elseif ($info.LastTaskResult -eq 0) { Ok "First backup done: $($last -replace '^\S+ \S+\s+OK\s+', '')" }
            else { throw "The first backup failed: $($last -replace '^\S+ \S+\s+FAILED\s+', '')" }
        } catch {
            Warn "Automatic backup: $($_.Exception.Message)"
            if ($Gui) { Write-Host "##WARN|Automatic backup: $($_.Exception.Message)" }
        }
    }

    # ------------------------------------------------------------------ 10. site
    Step "Creating the IIS site on port $Port" 82
    & $appcmd list site /name:"$SiteName" *> $null
    if ($LASTEXITCODE -ne 0) {
        & $appcmd add site /name:"$SiteName" /bindings:"http/*:${Port}:" /physicalPath:"$InstallDir" | Out-Null
    } else {
        & $appcmd set site /site.name:"$SiteName" /bindings:"http/*:${Port}:" | Out-Null
        & $appcmd set vdir /vdir.name:"$SiteName/" /physicalPath:"$InstallDir" | Out-Null
    }
    & $appcmd set app /app.name:"$SiteName/" /applicationPool:$PoolName /preloadEnabled:true | Out-Null
    & $appcmd set config "$SiteName" /section:system.webServer/webSocket /enabled:true /commit:apphost *> $null
    & $appcmd start apppool /apppool.name:$PoolName *> $null
    & $appcmd start site /site.name:"$SiteName" *> $null
    Ok "Site '$SiteName' -> http://localhost:$Port"

    # ------------------------------------------------------------------ 11. extras
    Step "Firewall, shortcut and uninstaller" 88
    if (-not $NoFirewall) {
        $rule = "NAS POS Pro (TCP $Port)"
        if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
        }
        Ok "Firewall opened for other counters on the local network (private/domain networks only)."
    }
    $shortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'NAS POS Pro.url'
    [IO.File]::WriteAllText($shortcut, "[InternetShortcut]`r`nURL=http://localhost:$Port/`r`n")

    # Uninstaller + Programs and Features entry
    Copy-Item (Join-Path $here 'uninstall.ps1') (Join-Path $DataDir 'uninstall.ps1') -Force
    $state = [ordered]@{ InstallDir = $InstallDir; Port = $Port; SqlInstance = $SqlInstance; DbName = $DbName; AppSqlUser = $AppSqlUser; SiteName = $SiteName; PoolName = $PoolName
                         BackupInterval = $BackupInterval; BackupDir = $BackupDir; BackupTime = $BackupTime; BackupDay = $BackupDay; BackupKeepDays = $BackupKeepDays }
    [IO.File]::WriteAllText((Join-Path $DataDir 'install.json'), ($state | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    $version = (Get-Item (Join-Path $InstallDir 'NasPos.Api.dll')).VersionInfo.ProductVersion
    $uk = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NasPosPro'
    New-Item $uk -Force | Out-Null
    Set-ItemProperty $uk -Name DisplayName -Value 'NAS POS Pro'
    Set-ItemProperty $uk -Name DisplayVersion -Value ($version -replace '\+.*$', '')
    Set-ItemProperty $uk -Name Publisher -Value 'NAS POS Pro'
    Set-ItemProperty $uk -Name InstallLocation -Value $InstallDir
    Set-ItemProperty $uk -Name URLInfoAbout -Value "http://localhost:$Port/"
    Set-ItemProperty $uk -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$DataDir\uninstall.ps1`""
    Set-ItemProperty $uk -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $uk -Name NoRepair -Value 1 -Type DWord

    # ------------------------------------------------------------------ 12. warm-up
    Step "Starting the application" 92
    $ok = $false
    for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
        try { $r = Invoke-WebRequest "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 10; $ok = $r.StatusCode -eq 200 } catch { Start-Sleep -Seconds 2 }
    }
    if ($ok) { Ok "NAS POS Pro is running." }
    else { Warn "The site did not answer yet. Open http://localhost:$Port in a minute; if it shows an error, see the Windows Event Log (Application) and $log." }

    if ($Gui) {
        Write-Host "##PROGRESS|100|Done"
        Write-Host "##URL|http://localhost:$Port/"
        if ($restartNeeded) { Write-Host "##RESTART" }
    }
    Write-Host ""
    Write-Host "  Done. Open http://localhost:$Port and sign in (a new database opens the company setup screen first)." -ForegroundColor Green
    Write-Host "  Other computers on the network: http://$($env:COMPUTERNAME):$Port" -ForegroundColor Green
    Write-Host "  SQL Server $SqlInstance, database $DbName, login $AppSqlUser" -ForegroundColor Green
    if ($restartNeeded) { Write-Host "  Windows asked for a restart to finish installing components. Please restart soon." -ForegroundColor Yellow }
    if ($ok -and -not $NoBrowser) { Start-Process "http://localhost:$Port/" }
    Stop-Transcript | Out-Null
    exit 0
}
catch {
    Write-Host ""
    if ($Gui) { Write-Host "##ERROR|$($_.Exception.Message)" }
    Write-Host "  SETUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Log: $log" -ForegroundColor Red
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}
