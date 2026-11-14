<#
  NAS POS Pro - uninstaller. Removes the IIS site, app pool, files, firewall rule, shortcut and the Programs and Features entry.
  The database is KEPT unless -RemoveDatabase is given (it holds your bills and accounts).
#>
[CmdletBinding()]
param([switch]$RemoveDatabase, [switch]$Quiet)

$ErrorActionPreference = 'Continue'
$DataDir = Join-Path $env:ProgramData 'NasPosPro'
$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($RemoveDatabase) { $argList += '-RemoveDatabase' }
    if ($Quiet) { $argList += '-Quiet' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

$state = @{ InstallDir = "$env:SystemDrive\inetpub\NasPosPro"; Port = 4300; SiteName = 'NAS POS Pro'; PoolName = 'NasPosPro'; SqlInstance = ''; DbName = 'NASDB'; AppSqlUser = '' }
$stateFile = Join-Path $DataDir 'install.json'
if (Test-Path $stateFile) { (Get-Content $stateFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $state[$_.Name] = $_.Value } }

Write-Host "Uninstalling NAS POS Pro from $($state.InstallDir)" -ForegroundColor Cyan
if (-not $Quiet) {
    $a = Read-Host "Continue? (y/N)"
    if ($a -notmatch '^(y|yes)$') { exit 1 }
}

if (Test-Path $appcmd) {
    & $appcmd stop site /site.name:"$($state.SiteName)" *> $null
    & $appcmd delete site "$($state.SiteName)" *> $null
    & $appcmd stop apppool /apppool.name:"$($state.PoolName)" *> $null
    & $appcmd delete apppool "$($state.PoolName)" *> $null
    Write-Host "  IIS site and app pool removed."
}
Start-Sleep -Seconds 2
if (Test-Path $state.InstallDir) { Remove-Item $state.InstallDir -Recurse -Force; Write-Host "  Files removed." }
Unregister-ScheduledTask -TaskName 'NAS POS Pro Backup' -Confirm:$false -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName "NAS POS Pro (TCP $($state.Port))" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'NAS POS Pro.url') -ErrorAction SilentlyContinue
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NasPosPro' -Recurse -ErrorAction SilentlyContinue

if ($state.SqlInstance) {
    $login = "IIS APPPOOL\$($state.PoolName)"
    $sql = "IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$login') DROP LOGIN [$login];"
    if ($RemoveDatabase) {
        $sql = "IF DB_ID(N'$($state.DbName)') IS NOT NULL BEGIN ALTER DATABASE [$($state.DbName)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$($state.DbName)]; END; " + $sql
        if ($state.AppSqlUser) { $sql += " IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$($state.AppSqlUser)') DROP LOGIN [$($state.AppSqlUser)];" }
    }
    try {
        $cn = New-Object System.Data.SqlClient.SqlConnection "Server=$($state.SqlInstance);Database=master;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15"
        $cn.Open(); $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; [void]$cmd.ExecuteNonQuery(); $cn.Dispose()
        if ($RemoveDatabase) { Write-Host "  Database $($state.DbName) deleted." } else { Write-Host "  Database $($state.DbName) kept on $($state.SqlInstance)." }
    } catch { Write-Host "  Could not update SQL Server: $($_.Exception.Message)" -ForegroundColor Yellow }
}

Write-Host "NAS POS Pro has been removed. (IIS, SQL Server and the .NET Hosting Bundle were left installed.)" -ForegroundColor Green
if ($state.BackupDir -and (Test-Path $state.BackupDir)) { Write-Host "Database backups were kept in $($state.BackupDir)." -ForegroundColor Green }
Remove-Item $stateFile -ErrorAction SilentlyContinue
if (-not $Quiet) { Read-Host "Press Enter to close" | Out-Null }
