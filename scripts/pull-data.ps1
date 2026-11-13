<#
.SYNOPSIS
  Copies the hosted Smooth Budget database (Render) to a separate local database, then runs the data check on it.

.DESCRIPTION
  Run it yourself from the repo root:  pwsh scripts/pull-data.ps1
  It asks for the hosted database's External Database URL (Render: your database -> Connect -> External). Nothing you
  type is saved, and the hosted database is only read.

  The copy goes into its own local database, "smoothbudget_check" (replaced each run). Your local development database
  isn't touched. Sign-ins, bank tokens, push devices and invites are blanked in the copy right after it's restored, so it
  holds your transactions and budget but nothing that can reach your bank or your account.

  The check's results are printed and saved to private\pull\check.txt (git-ignored). The dump file is deleted at the end.

  Needs PostgreSQL 17's command-line tools (pg_dump, pg_restore, psql), installed with PostgreSQL on this machine.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$copy = 'smoothbudget_check'

# PostgreSQL's tools: on PATH, or the default Windows install folder.
$bin = @('C:\Program Files\PostgreSQL\17\bin') | Where-Object { Test-Path (Join-Path $_ 'pg_dump.exe') } | Select-Object -First 1
function Tool($name) {
  $cmd = Get-Command $name -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  if ($bin) { return Join-Path $bin "$name.exe" }
  throw "Couldn't find $name. Install PostgreSQL 17's command-line tools."
}
$pgDump = Tool 'pg_dump'; $pgRestore = Tool 'pg_restore'; $psql = Tool 'psql'

# The local server, from .env (ConnectionStrings__Default=Host=...;Port=...;Database=...;Username=...;Password=...).
$line = Get-Content (Join-Path $root '.env') | Where-Object { $_ -like 'ConnectionStrings__Default=*' } | Select-Object -First 1
if (-not $line) { throw 'ConnectionStrings__Default is missing from .env.' }
$cs = @{}
foreach ($part in ($line.Substring('ConnectionStrings__Default='.Length) -split ';')) {
  $kv = $part -split '=', 2
  if ($kv.Count -eq 2) { $cs[$kv[0].Trim().ToLowerInvariant()] = $kv[1].Trim() }
}
$local = @('-h', $cs['host'], '-p', ($cs['port'] ?? '5432'), '-U', $cs['username'])

# SMOOTHBUDGET_PULL_URL lets a test run it without typing; normally you paste the URL when asked.
$source = $env:SMOOTHBUDGET_PULL_URL
if (-not $source) { $source = Read-Host 'Paste the hosted database''s External Database URL (postgresql://...)' }
if ($source -notmatch '^postgres(ql)?://') { throw 'That doesn''t look like a postgresql:// URL.' }
if ($source -notmatch 'sslmode=' -and $source -notmatch '@(localhost|127\.0\.0\.1)[:/]') { $source += $(if ($source.Contains('?')) { '&' } else { '?' }) + 'sslmode=require' }

$dir = Join-Path $root 'private\pull'
New-Item -ItemType Directory -Force $dir | Out-Null
$dump = Join-Path $dir 'smoothbudget-live.dump'
$report = Join-Path $dir 'check.txt'

try {
  Write-Host '1/4 Copying the hosted database (read only)...'
  & $pgDump $source --format=custom --no-owner --no-privileges -f $dump
  if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed. Check the URL (use the External one).' }

  Write-Host "2/4 Restoring into the local database `"$copy`" (your development database isn't touched)..."
  $env:PGPASSWORD = $cs['password']
  & $psql @local -d postgres -X -q -c "drop database if exists $copy with (force)" -c "create database $copy"
  if ($LASTEXITCODE -ne 0) { throw "Couldn't create the local database $copy." }
  & $pgRestore @local -d $copy --no-owner --no-privileges $dump
  if ($LASTEXITCODE -ne 0) { Write-Warning 'pg_restore reported problems. Check the output above.' }

  Write-Host '3/4 Blanking sign-ins, bank tokens, push devices and invites in the copy...'
  $scrub = @'
\set ON_ERROR_STOP on
update "PlaidItems" set "AccessTokenEncrypted" = '', "Cursor" = null;
update "AspNetUsers" set "PasswordHash" = null, "SecurityStamp" = null, "ConcurrencyStamp" = null;
truncate "AspNetUserTokens", "AspNetUserLogins", "DataProtectionKeys", "PushDevices", "FriendInvites";
'@
  $scrub | & $psql @local -d $copy -X -q
  if ($LASTEXITCODE -ne 0) { throw 'Blanking the secrets failed, so the copy was removed.' }

  Write-Host '4/4 Running the data check...'
  & $psql @local -d $copy -X -f (Join-Path $PSScriptRoot 'check-data.sql') | Tee-Object -FilePath $report
  Write-Host ''
  Write-Host "Saved to $report. The copy stays in the local database `"$copy`" until the next run."
}
catch {
  # A copy that still holds secrets must not be left behind.
  $env:PGPASSWORD = $cs['password']
  & $psql @local -d postgres -X -q -c "drop database if exists $copy with (force)" 2>$null
  throw
}
finally {
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
  if (Test-Path $dump) { Remove-Item $dump; Write-Host 'Deleted the local dump.' }
}
