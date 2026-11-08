<#
.SYNOPSIS
  Copies your local Smooth Budget database to the hosted one (Render), then removes every login except yours.

.DESCRIPTION
  Run it yourself from the repo root:  pwsh scripts/move-data.ps1
  It asks for the hosted database's External Database URL (Render: your database -> Connect -> External) and your
  email. Nothing you type is saved. The dump is written to the git-ignored private/ folder and deleted at the end
  unless you choose to keep it. See DEPLOY.md, step 4.

  Needs PostgreSQL 17's command-line tools (pg_dump, pg_restore, psql), installed with PostgreSQL on this machine.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# PostgreSQL's tools: on PATH, or the default Windows install folder.
$bin = @('C:\Program Files\PostgreSQL\17\bin') | Where-Object { Test-Path (Join-Path $_ 'pg_dump.exe') } | Select-Object -First 1
function Tool($name) {
  $cmd = Get-Command $name -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  if ($bin) { return Join-Path $bin "$name.exe" }
  throw "Couldn't find $name. Install PostgreSQL 17's command-line tools."
}
$pgDump = Tool 'pg_dump'; $pgRestore = Tool 'pg_restore'; $psql = Tool 'psql'

# The local database, from .env (ConnectionStrings__Default=Host=...;Port=...;Database=...;Username=...;Password=...).
$line = Get-Content (Join-Path $root '.env') | Where-Object { $_ -like 'ConnectionStrings__Default=*' } | Select-Object -First 1
if (-not $line) { throw 'ConnectionStrings__Default is missing from .env.' }
$cs = @{}
foreach ($part in ($line.Substring('ConnectionStrings__Default='.Length) -split ';')) {
  $kv = $part -split '=', 2
  if ($kv.Count -eq 2) { $cs[$kv[0].Trim().ToLowerInvariant()] = $kv[1].Trim() }
}

$target = Read-Host 'Paste the hosted database''s External Database URL (postgresql://...)'
if ($target -notmatch '^postgres(ql)?://') { throw 'That doesn''t look like a postgresql:// URL.' }
if ($target -notmatch 'sslmode=') { $target += $(if ($target.Contains('?')) { '&' } else { '?' }) + 'sslmode=require' }
$email = Read-Host 'Your Smooth Budget sign-in email (every other login is removed on the host)'

$dir = Join-Path $root 'private\move'
New-Item -ItemType Directory -Force $dir | Out-Null
$dump = Join-Path $dir 'smoothbudget.dump'

$keepDump = $false
try {
Write-Host '1/4 Copying your local database...'
$env:PGPASSWORD = $cs['password']
& $pgDump -h $cs['host'] -p ($cs['port'] ?? '5432') -U $cs['username'] -d $cs['database'] --format=custom --no-owner --no-privileges -f $dump
if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed.' }
Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue

Write-Host '2/4 Checking the hosted database is reachable...'
& $psql $target -tA -c 'select 1' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Couldn''t connect to the hosted database. Check the URL (use the External one).' }

Write-Host '3/4 Restoring into the hosted database (this replaces what''s there)...'
& $pgRestore --clean --if-exists --no-owner --no-privileges -d $target $dump
if ($LASTEXITCODE -ne 0) { Write-Warning 'pg_restore reported problems (warnings about objects that didn''t exist yet are normal). Check the output above.' }

Write-Host '4/4 Removing every login except yours...'
$sql = @'
\set ON_ERROR_STOP on
select count(*) as your_login from "AspNetUsers" where lower("Email") = lower(:'keep') \gset
\if :your_login
  delete from "AspNetUsers" where lower("Email") <> lower(:'keep');
  select count(*) as logins_left from "AspNetUsers";
\else
  \echo 'Your email was not found, so no logins were removed.'
  \quit 3
\endif
'@
$sql | & $psql $target -v "keep=$email" -X -q
if ($LASTEXITCODE -eq 3) { throw "No login with $email exists in the copied data. Nothing was removed; run the script again with the right email." }
if ($LASTEXITCODE -ne 0) { throw 'Removing the other logins failed.' }

$keepDump = (Read-Host 'Done. Keep the local dump file in private\move? (y/N)') -eq 'y'
Write-Host 'Now restart the web service in Render (Manual Deploy -> Restart service), then sign in.'
}
finally {
  # The dump holds your real data: it's removed whether the move worked or not, unless you asked to keep it.
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
  if (-not $keepDump -and (Test-Path $dump)) { Remove-Item $dump; Write-Host 'Deleted the local dump.' }
}
