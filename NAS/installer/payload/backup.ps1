<#
  NAS POS Pro - scheduled database backup.
  Run by the "NAS POS Pro Backup" scheduled task as SYSTEM (set up by install.ps1); settings in backup.json next to this file.
  Signs in with the application's SQL login (db_owner of the database, read from the app's appsettings.Production.json).

  SQL Server writes the .bak with its own service account, which only has rights on the staging folder below; this script
  (SYSTEM) then moves it to the chosen folder - so any local disk, USB drive or share writable by SYSTEM works as a destination.
#>
$ErrorActionPreference = 'Stop'
$dataDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = Join-Path $dataDir 'backup.log'
function Log([string]$m) { Add-Content -Path $logFile -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m) -Encoding UTF8 }

$tmp = $null
try {
    $cfg = Get-Content (Join-Path $dataDir 'backup.json') -Raw | ConvertFrom-Json
    $settings = Get-Content (Join-Path $cfg.InstallDir 'appsettings.Production.json') -Raw | ConvertFrom-Json
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder $settings.ConnectionStrings.Default
    $b['Application Name'] = 'NAS POS Pro Backup'
    $db = $b['Initial Catalog']

    $stage = Join-Path $dataDir 'backup-staging'
    $name = '{0}_{1:yyyyMMdd_HHmmss}.bak' -f $db, (Get-Date)
    $tmp = Join-Path $stage $name
    $sqlPath = $tmp.Replace("'", "''")

    $cn = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand()
        $cmd.CommandTimeout = 0
        $cmd.CommandText = "BACKUP DATABASE [$db] TO DISK = N'$sqlPath' WITH INIT, CHECKSUM, NAME = N'NAS POS Pro scheduled backup';"
        [void]$cmd.ExecuteNonQuery()
    } finally { $cn.Dispose() }

    New-Item -ItemType Directory -Force -Path $cfg.BackupDir | Out-Null
    $dest = Join-Path $cfg.BackupDir $name
    Move-Item -LiteralPath $tmp -Destination $dest -Force
    $tmp = $null
    $mb = [math]::Round((Get-Item -LiteralPath $dest).Length / 1MB, 1)
    Log "OK      $dest ($mb MB)"

    # Retention: delete this database's backups older than KeepDays, but never the newest 3.
    $old = @(Get-ChildItem -LiteralPath $cfg.BackupDir -Filter "$($db)_*.bak" -File | Sort-Object LastWriteTime -Descending |
             Select-Object -Skip 3 | Where-Object LastWriteTime -lt (Get-Date).AddDays(-[int]$cfg.KeepDays))
    foreach ($f in $old) { Remove-Item -LiteralPath $f.FullName -Force; Log "Deleted $($f.FullName) (older than $($cfg.KeepDays) days)" }
}
catch {
    Log "FAILED  $($_.Exception.Message)"
    if ($tmp -and (Test-Path -LiteralPath $tmp)) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    exit 1
}
finally {
    # Keep the log small.
    try {
        $lines = @(Get-Content -LiteralPath $logFile -Encoding UTF8)
        if ($lines.Count -gt 1000) { $lines | Select-Object -Last 500 | Set-Content -LiteralPath $logFile -Encoding UTF8 }
    } catch { }
}
exit 0
