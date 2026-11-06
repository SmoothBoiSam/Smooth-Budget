# Smooth Budget

A phone-first budgeting app whose Home screen answers one question: **how much do I have left to spend this month?**
The full spec (`SPEC.md`) stays out of the repo because its appendices contain real pay figures. `DECISIONS.md` records the choices made where the spec is silent.

**Status:** Milestone 2 (data in):
- **Milestone 1:** auth, database, CI, fake-data seed, and a Home screen showing Left to Spend.
- **Milestone 2:** CSV import for Chase checking, Chase credit and Apple Card; categorization by bank category and merchant rules; card payment and transfer detection; and an inbox for transactions that still need a category.

## Stack

- **Backend:** ASP.NET Core (.NET 10) minimal API, EF Core, ASP.NET Core Identity (cookie auth), PostgreSQL 17
- **Frontend:** React 19 + TypeScript, Vite, Tailwind CSS v4
- **Money math:** `backend/src/SmoothBudget.Core`, pure C# with unit tests. The frontend only displays results.

```
backend/src/SmoothBudget.Core        Budget-month calendar, Left to Spend calculator,
                                     CSV parsing, merchant cleanup, dedup, categorizer, transfer detector
backend/src/SmoothBudget.Api         EF Core model + migrations, auth, endpoints, import pipeline, demo seeder
backend/tests/SmoothBudget.Core.Tests    Unit tests for the money math and import logic
backend/tests/SmoothBudget.Api.Tests     End-to-end API tests against a throwaway Postgres DB
frontend/                            Vite React app (login, Home, Budget/inbox, More/import)
scripts/seed.ps1, scripts/seed.sh    Load fake demo data
```

## Prerequisites

- .NET SDK 10
- Node.js 22+ (LTS)
- PostgreSQL 17 running locally, with a database and login role for the app

## First-time setup

1. Copy `.env.example` to `.env` and fill in `ConnectionStrings__Default` and `SEED_DEMO_PASSWORD`.
2. Install the tools and dependencies:
   ```bash
   dotnet tool restore
   npm --prefix frontend install
   ```
3. Create the schema and load fake data (safe to re-run: it resets only the demo household):
   ```bash
   ./scripts/seed.sh        # or: pwsh scripts/seed.ps1
   ```

## Run

Run these in two terminals:

```bash
dotnet run --project backend/src/SmoothBudget.Api --launch-profile http
```

```bash
npm --prefix frontend run dev
```

Open http://localhost:5173 and sign in as `demo@smoothbudget.test` with the `SEED_DEMO_PASSWORD` from your `.env`.
On a phone on the same Wi-Fi, run `npm --prefix frontend run dev -- --host` and open the printed network URL.

### Importing your bank exports

1. Download the CSV from Chase (checking or credit card) or Apple Card (Wallet → Card Balance → Statements → Export Transactions → CSV).
2. In the app, open **More → Import a CSV**, add the account once (name, type, optionally the last 4 digits), pick the file, and import.
3. Card payments and transfers are detected automatically. Anything without a category shows up under **Budget → Needs a category**. Tap a transaction to set its category, and tick "Always use this category" to create a merchant rule.

Re-importing an overlapping export is safe: rows already in the account are skipped. Each import can be undone from **Recent imports**.
Keep your real exports outside the repo (or under `/private/`, which is git-ignored).

## Test

```bash
dotnet test SmoothBudget.slnx
```

```bash
npm --prefix frontend run lint
```

```bash
npm --prefix frontend run build
```

The API tests need PostgreSQL. They read `ConnectionStrings__Default` from `.env` (or the environment), and create and drop their own `smoothbudget_test_*` database. The role needs `CREATEDB`.

CI (`.github/workflows/ci.yml`) runs all of the above on every push and PR, with Postgres as a service container. It also fails if the EF model has changes that no migration covers.

## Database migrations

```bash
dotnet ef migrations add <Name> --project backend/src/SmoothBudget.Api --output-dir Data/Migrations
```

In Development, migrations apply automatically on startup.

## API

| Method | Path | Notes |
|---|---|---|
| POST | `/api/auth/register` | `{ email, password }`. Creates a user and their household, and signs in. |
| POST | `/api/auth/login` | `{ email, password }`. Sets the auth cookie. |
| POST | `/api/auth/logout` | |
| GET | `/api/auth/me` | |
| GET | `/api/home/summary` | Left to Spend, status, days left, safe per day, carried over, over-budget line, uncategorized count |
| GET / POST | `/api/accounts` | List, or add `{ name, type: Checking\|Savings\|CreditCard, mask: "1234" }` |
| POST | `/api/import/csv` | Multipart: `file` and `accountId`. Returns counts and any unreadable rows. |
| GET | `/api/import/batches` | Recent imports |
| DELETE | `/api/import/batches/{id}` | Undo an import |
| GET | `/api/transactions` | Current budget month, or `?from=YYYY-MM-DD&to=YYYY-MM-DD` |
| GET | `/api/transactions/inbox`, `/api/transactions/inbox/count` | Transactions that need a category |
| PATCH | `/api/transactions/{id}` | `{ categoryId, isTransfer, rulePattern? }`. Your choice locks the row. |
| GET | `/api/budget/categories` | |
| GET | `/api/budget/summary` | Every category grouped by type, with limit, spent/paid, remaining and health, plus Left to Spend |
| GET / DELETE | `/api/rules`, `/api/rules/{id}` | Merchant rules |
| GET | `/api/health` | |

## Data safety

All seed data is invented. Never commit `.env`, real exports, pay stubs or schedules. `.gitignore` blocks `*.csv`, `*.pdf`, `/data/` and `/private/`.
