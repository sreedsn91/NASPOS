<#
  NAS POS Pro - IIS installer.
  Runs from the extracted setup folder (NasPosSetup.exe does this, elevated). Safe to re-run: an existing install is upgraded in place
  (settings and JWT key are kept, migrations are applied, the database is never dropped).

  Steps: IIS features -> ASP.NET Core Hosting Bundle -> SQL Server (use a local instance, or install SQL Express)
         -> copy files -> settings -> IIS app pool + site on :Port -> database migrations -> app-pool DB login -> firewall, shortcut.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:SystemDrive\inetpub\NasPosPro",
    [int]$Port = 4300,
    [string]$SqlInstance = "",          # e.g. "localhost", ".\SQLEXPRESS". Empty = auto-detect.
    [string]$DbName = "NasPos",
    [string]$SqlUser = "",              # optional SQL admin login for install (default: Windows login of the installer)
    [string]$SqlPassword = "",
    [switch]$NoSqlInstall,              # fail instead of installing SQL Express when no instance exists
    [switch]$NoFirewall,
    [switch]$NoBrowser,
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

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$log = Join-Path $DataDir ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $log -Force | Out-Null

function Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Info([string]$text) { Write-Host "    $text" }
function Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }
function Fail([string]$text) { throw $text }
function Ask([string]$question, [string]$default) {
    if ($Quiet) { return $default }
    $a = Read-Host "    $question [$default]"
    if ([string]::IsNullOrWhiteSpace($a)) { return $default } else { return $a.Trim() }
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

    Write-Host ""
    Write-Host "  NAS POS Pro - Setup" -ForegroundColor White
    Write-Host "  Install folder : $InstallDir"
    Write-Host "  Web address    : http://localhost:$Port"
    Write-Host "  Log            : $log"

    # ------------------------------------------------------------------ 1. IIS
    Step "Enabling IIS (Internet Information Services)"
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
    Step "Checking the .NET 9 Hosting Bundle (ASP.NET Core Module for IIS)"
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
    Step "Finding SQL Server"
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

    $instances = @(Get-SqlInstances)
    if ([string]::IsNullOrWhiteSpace($SqlInstance)) {
        if ($instances.Count -eq 0) {
            if ($NoSqlInstall) { Fail "No SQL Server instance found on this computer." }
            Step "Installing SQL Server 2022 Express (instance SQLEXPRESS)"
            $media = Join-Path $here 'prereqs\SQLEXPR_x64_ENU.exe'
            $work = Join-Path $env:TEMP 'NasPosSql'
            New-Item -ItemType Directory -Force -Path $work | Out-Null
            if (-not (Test-Path $media)) {
                Info "Downloading SQL Server Express from Microsoft (about 250 MB)..."
                $ssei = Join-Path $work 'SQL2022-SSEI-Expr.exe'
                Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?linkid=2216019' -OutFile $ssei -UseBasicParsing
                $p = Start-Process $ssei -ArgumentList '/ACTION=Download', "/MEDIAPATH=$work", '/MEDIATYPE=Core', '/QUIET' -Wait -PassThru
                $media = Get-ChildItem $work -Filter 'SQLEXPR*.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
                if (-not $media) { Fail "SQL Server Express download failed (exit code $($p.ExitCode))." }
            }
            Info "Extracting..."
            $extract = Join-Path $work 'setup'
            Start-Process $media -ArgumentList '/Q', "/X:$extract" -Wait | Out-Null
            Info "Installing (this can take 5-15 minutes)..."
            $sqlArgs = '/Q', '/ACTION=Install', '/FEATURES=SQLENGINE', '/INSTANCENAME=SQLEXPRESS', '/SQLSYSADMINACCOUNTS="BUILTIN\Administrators"',
                       '/TCPENABLED=0', '/UPDATEENABLED=0', '/IACCEPTSQLSERVERLICENSETERMS'
            $p = Start-Process (Join-Path $extract 'setup.exe') -ArgumentList $sqlArgs -Wait -PassThru
            if ($p.ExitCode -eq 3010) { $restartNeeded = $true } elseif ($p.ExitCode -ne 0) { Fail "SQL Server Express install failed (exit code $($p.ExitCode)). See %ProgramFiles%\Microsoft SQL Server\*\Setup Bootstrap\Log." }
            Ok "SQL Server Express installed."
            $instances = @(Get-SqlInstances)
            $SqlInstance = 'localhost\SQLEXPRESS'
        } else {
            $running = @($instances | Where-Object Status -eq 'Running')
            $default = if ($running.Count -gt 0) { $running[0].Server } else { $instances[0].Server }
            Info "Found:"
            $instances | ForEach-Object { Info ("  {0,-32} {1}" -f $_.Server, $_.Status) }
            $SqlInstance = Ask "SQL Server to use" $default
        }
    }
    # Make sure the chosen instance's service is running and starts with Windows.
    $chosen = $instances | Where-Object { $_.Server -ieq $SqlInstance -or ".\$($_.Name)" -ieq $SqlInstance -or ($_.Name -eq 'MSSQLSERVER' -and $SqlInstance -in '.', '(local)', 'localhost') } | Select-Object -First 1
    if ($chosen) {
        Set-Service $chosen.Service -StartupType Automatic
        if ((Get-Service $chosen.Service).Status -ne 'Running') { Info "Starting $($chosen.Service)..."; Start-Service $chosen.Service }
    }

    $adminCs = if ($SqlUser) { "Server=$SqlInstance;Database=master;User ID=$SqlUser;Password=$SqlPassword;TrustServerCertificate=True;Connect Timeout=15" }
               else { "Server=$SqlInstance;Database=master;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15" }
    $canCreate = $null
    for ($try = 0; $try -lt 3 -and $null -eq $canCreate; $try++) {
        try {
            $canCreate = Invoke-Sql $adminCs "SELECT CASE WHEN DB_ID(N'$DbName') IS NOT NULL OR HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE') = 1 THEN 1 ELSE 0 END"
        } catch {
            Warn "Could not connect to $SqlInstance : $($_.Exception.Message)"
            if ($Quiet) { throw }
            $SqlUser = Ask "SQL admin login (e.g. sa)" "sa"
            $sec = Read-Host "    Password for $SqlUser" -AsSecureString
            $SqlPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
            $adminCs = "Server=$SqlInstance;Database=master;User ID=$SqlUser;Password=$SqlPassword;TrustServerCertificate=True;Connect Timeout=15"
        }
    }
    if ($null -eq $canCreate) { Fail "Could not connect to SQL Server $SqlInstance." }
    if ($canCreate -ne 1) { Fail "The login used by setup cannot create the database '$DbName'. Run setup as a SQL Server administrator or pass -SqlUser/-SqlPassword." }
    Ok "Using $SqlInstance, database $DbName."

    # ------------------------------------------------------------------ 4. port check
    Step "Checking port $Port"
    $sites = & $appcmd list site 2>$null
    $clash = @($sites | Where-Object { $_ -match "[:*]$Port[:/]" -and $_ -notmatch [regex]::Escape("""$SiteName""") })
    if ($clash.Count -gt 0) { Fail "Port $Port is already used by another IIS site: $($clash -join '; '). Re-run with -Port <other>." }
    $listen = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Where-Object OwningProcess -ne 4
    if ($listen) {
        $pn = (Get-Process -Id $listen[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
        Fail "Port $Port is in use by '$pn' (PID $($listen[0].OwningProcess)). Stop it or re-run with -Port <other>."
    }
    Ok "Port $Port is free."

    # ------------------------------------------------------------------ 5. IIS app pool (needed before files ACL and the DB login)
    Step "Configuring IIS"
    & $appcmd list apppool /name:$PoolName *> $null
    if ($LASTEXITCODE -ne 0) { & $appcmd add apppool /name:$PoolName | Out-Null; Info "App pool '$PoolName' created." }
    else { & $appcmd stop apppool /apppool.name:$PoolName *> $null; Info "App pool '$PoolName' stopped for upgrade." }
    & $appcmd set apppool /apppool.name:$PoolName /managedRuntimeVersion:"" /managedPipelineMode:Integrated /startMode:AlwaysRunning `
        /processModel.identityType:ApplicationPoolIdentity /processModel.loadUserProfile:true /processModel.idleTimeout:00:00:00 `
        /recycling.periodicRestart.time:00:00:00 | Out-Null
    Start-Sleep -Seconds 2

    # ------------------------------------------------------------------ 6. files
    Step "Copying application files"
    New-Item -ItemType Directory -Force -Path $InstallDir, (Join-Path $InstallDir 'logs') | Out-Null
    & robocopy (Join-Path $here 'app') $InstallDir /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP /XF appsettings.Production.json /XD logs | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Copying files failed (robocopy exit code $LASTEXITCODE)." }
    Ok "Files copied to $InstallDir."

    # ------------------------------------------------------------------ 7. settings (JWT key is generated once and kept on upgrade)
    Step "Writing settings"
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
    $appCs = "Server=$SqlInstance;Database=$DbName;Integrated Security=True;TrustServerCertificate=True;Application Name=NAS POS Pro"
    $settings = [ordered]@{
        ConnectionStrings = [ordered]@{ Default = $appCs }
        Jwt = [ordered]@{ SigningKey = $signingKey }
        Database = [ordered]@{ MigrateOnStartup = $false }
        # Plain HTTP on the shop LAN: no HTTPS redirect, refresh cookie without the Secure flag.
        Security = [ordered]@{ HttpsRedirect = $false; SecureCookies = $false }
    }
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    # The file holds the token signing key: only admins, SYSTEM and the app pool may read it.
    & icacls $settingsPath /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(F)" "${PoolIdentity}:(R)" | Out-Null
    & icacls $InstallDir /grant "${PoolIdentity}:(OI)(CI)(RX)" | Out-Null
    & icacls (Join-Path $InstallDir 'logs') /grant "${PoolIdentity}:(OI)(CI)(M)" | Out-Null
    Ok "Settings saved (existing keys kept)."

    # ------------------------------------------------------------------ 8. database
    Step "Creating / upgrading the database"
    $migrateCs = $adminCs -replace 'Database=master', "Database=$DbName"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:ConnectionStrings__Default = $migrateCs
    Push-Location $InstallDir
    try { & $dotnet (Join-Path $InstallDir 'NasPos.Api.dll') --migrate; $code = $LASTEXITCODE } finally { Pop-Location; Remove-Item Env:ConnectionStrings__Default }
    if ($code -ne 0) { Fail "Database migration failed (exit code $code). See the messages above." }
    Ok "Database is up to date."

    Info "Granting the web app access to the database..."
    $grant = @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$PoolIdentity')
    CREATE LOGIN [$PoolIdentity] FROM WINDOWS WITH DEFAULT_DATABASE = [$DbName];
USE [$DbName];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$PoolIdentity')
    CREATE USER [$PoolIdentity] FOR LOGIN [$PoolIdentity];
ALTER ROLE db_datareader ADD MEMBER [$PoolIdentity];
ALTER ROLE db_datawriter ADD MEMBER [$PoolIdentity];
SELECT 1;
"@
    Invoke-Sql $adminCs $grant | Out-Null
    Ok "Read/write access granted to $PoolIdentity (no schema rights)."

    # ------------------------------------------------------------------ 9. site
    Step "Creating the IIS site on port $Port"
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

    # ------------------------------------------------------------------ 10. extras
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
    $state = [ordered]@{ InstallDir = $InstallDir; Port = $Port; SqlInstance = $SqlInstance; DbName = $DbName; SiteName = $SiteName; PoolName = $PoolName }
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

    # ------------------------------------------------------------------ 11. warm-up
    Step "Starting the application"
    $ok = $false
    for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
        try { $r = Invoke-WebRequest "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 10; $ok = $r.StatusCode -eq 200 } catch { Start-Sleep -Seconds 2 }
    }
    if ($ok) { Ok "NAS POS Pro is running." }
    else { Warn "The site did not answer yet. Open http://localhost:$Port in a minute; if it shows an error, see the Windows Event Log (Application) and $log." }

    Write-Host ""
    Write-Host "  Done. Open http://localhost:$Port and sign in (a new database opens the company setup screen first)." -ForegroundColor Green
    Write-Host "  Other computers on the network: http://$($env:COMPUTERNAME):$Port" -ForegroundColor Green
    if ($restartNeeded) { Write-Host "  Windows asked for a restart to finish installing components. Please restart soon." -ForegroundColor Yellow }
    if ($ok -and -not $NoBrowser) { Start-Process "http://localhost:$Port/" }
    Stop-Transcript | Out-Null
    exit 0
}
catch {
    Write-Host ""
    Write-Host "  SETUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Log: $log" -ForegroundColor Red
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}
