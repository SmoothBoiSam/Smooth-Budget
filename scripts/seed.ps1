# Loads FAKE demo data: migrates the database, creates or resets demo@smoothbudget.test
# (password = SEED_DEMO_PASSWORD from .env), and wipes and re-seeds that demo household only.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $root 'backend/src/SmoothBudget.Api') --launch-profile http -- seed
