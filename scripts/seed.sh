#!/usr/bin/env sh
# Loads FAKE demo data: migrates the database, creates or resets demo@smoothbudget.test
# (password = SEED_DEMO_PASSWORD from .env), and wipes and re-seeds that demo household only.
set -e
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet run --project "$root/backend/src/SmoothBudget.Api" --launch-profile http -- seed
