# Smooth Budget

A phone-first budgeting app whose Home screen answers one question: **how much do I have left to spend this month?**
The full spec (`SPEC.md`) stays out of the repo because its appendices contain real pay figures. `DECISIONS.md` records the choices made where the spec is silent.

**Status:** Milestone 10 (polish), the last milestone in the spec:
- **Milestone 1:** auth, database, CI, fake-data seed, and a Home screen showing Left to Spend.
- **Milestone 2:** CSV import for Chase checking, Chase credit and Apple Card; categorization by bank category and merchant rules; card payment and transfer detection; and an inbox for transactions that still need a category.
- **Milestone 3:** Plaid Link, cursor-based sync, verified webhooks, background sync, pending-to-posted handling, CSV/Plaid overlap matching, and sync health on Home.
- **Milestone 4:** editable categories (this month only or from now on, hide, reorder), income vs planned, the budget generator, and month close with sweeps and carry-forward.
- **Since milestone 4:** categories discovered from your own transactions, every automatic category reviewed in its own Review tab, one budget line per bill, no double counting of card payments or transfers, and partner splits with a settle-up balance.
- **Milestone 5:** the Debt tab: cards followed from their accounts and loans by hand, APR and minimum from the bank where it shares them (Plaid Liabilities), the payoff engine (Avalanche by default, Snowball compared), debt-free date, payoff chart, an "extra each month" what-if slider, utilization and progress. The budget generator proposes card minimums plus what's left to the highest APR, and sweeps go to the highest APR.
- **Milestone 6:** bills and subscriptions found from your transactions (More → Bills & subscriptions): monthly and annual totals, price changes, charges that stopped, possible duplicates, rename, bill vs subscription, due day, dismiss, and "make it its own bill line". The next 3 bills show on Home, and the budget generator proposes bills at their current price.
- **Milestone 7:** the Partner screen (More → Partner): a fixed contribution (e.g. $800 toward Rent) shown as an offset on its bill line with your net share, matched to her transfers in a window (the 18th to 24th by default) with expected, received, different-amount and late states (late or short money comes out of Left to Spend). Other money between you has no tally: each transfer waits in Review until you say what it was for (paying you back for a category, her money, or your share of something).
- **Milestone 8:** the Savings tab: total saved, savings accounts and balances, goals with progress from their account balance and a projected date at your monthly amount or recent pace, a rainy-day fund created for you, a 12-month savings chart, and the order money left over goes in (rainy-day fund, high-APR debt, larger savings), which the budget generator follows.
- **Milestone 9:** the Payday tab: import the monthly SkedPlus+ schedule (the detailed text export, or the older CSV with estimated times), add past pay stub PDFs (reviewed before saving; the app learns your taxes and deductions from them), and see the next 3 checks with a low/likely/high range, a payday plan for each (bills due before the next payday, card payment, sweep, savings, spending money), and predicted vs actual once a check lands. Every pay rule is editable. Home shows the next payday.
- **Milestone 10:** installable on a phone's home screen (app icon, manifest and a service worker), opens to the last known Left to Spend when offline with a clear "last known numbers" notice, and the same loading placeholders and "Try again" errors on every tab.
- **Also:** dark mode (More → Settings → Appearance: System, Light or Dark).

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

### Install and offline (production build)

The service worker only runs in a production build. To try it locally, build and serve the frontend (the API keeps running as above):

```bash
npm --prefix frontend run build
```

```bash
npm --prefix frontend run preview -- --port 4173
```

Open http://localhost:4173, sign in and load Home once. Stop both servers and reload: the app opens to the last known Left to Spend, marked as offline. On a phone, installing needs the app served over HTTPS (once it's hosted): Safari's Share → **Add to Home Screen**, or Chrome's **Install app**.

### Importing your bank exports

1. Download the CSV from Chase (checking or credit card) or Apple Card (Wallet → Card Balance → Statements → Export Transactions → CSV).
2. In the app, open **More → Import a CSV**, add the account once (name, type, optionally the last 4 digits), pick the file, and import.
3. Card payments and transfers are detected automatically. Anything without a category shows up under **Budget → Needs a category**. Tap a transaction to set its category, and tick "Always use this category" to create a merchant rule.

Re-importing an overlapping export is safe: rows already in the account are skipped, including ones your bank already synced through Plaid (in either order). Each import can be undone from **Recent imports**.

### Connecting banks with Plaid

1. Put your Plaid keys in `.env`: `PLAID_CLIENT_ID`, `PLAID_SECRET`, `PLAID_ENV=sandbox`, and a `PLAID_TOKEN_KEY` (see `.env.example` for the one-line command that generates it).
2. Open **More → Bank connections**:
   - **Connect a bank** opens Plaid Link. In sandbox, sign in with Plaid's test login `user_good` / `pass_good`.
   - **Add Plaid's sandbox test bank (dev only)** links Plaid's test bank without the Link screen.
3. Choose, for each account at the bank, **Import as a new account**, **Same as …** (pick the account you already imported CSVs into, so nothing is counted twice), or **Don't import**.
4. Pull down on Home (or tap **Refresh**, or **Sync now**) to sync. A background sweep also syncs every 6 hours.

In sandbox, **Simulate new activity** makes Plaid post pending purchases, so you can watch them post without being counted twice.
Webhooks need a public HTTPS URL. Set `PLAID_WEBHOOK_URL` when the app is hosted.
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

Plaid is replaced by an in-process fake in every test, except `PlaidSandboxLiveTests`. That one talks to the real Plaid sandbox, runs only when `.env` has sandbox keys (it's skipped in CI), and takes about 1–2 minutes:

```bash
dotnet test SmoothBudget.slnx --filter "FullyQualifiedName~PlaidSandboxLiveTests"
```

`PrivateSampleTests` checks the pay engine against your real schedule and pay stubs. Put them in the git-ignored `private/pay` folder as `schedule.txt` (the detailed text export), `schedule.csv` and `stub1.pdf`, `stub2.pdf`, … and run:

```bash
dotnet test SmoothBudget.slnx --filter "FullyQualifiedName~PrivateSampleTests" --logger "console;verbosity=detailed"
```

It's skipped when the folder is missing (as in CI), and it never writes your numbers anywhere.

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
| GET | `/api/home/summary` | Left to Spend, status, days left, safe per day, carried over, over-budget line, uncategorized count, sync health |
| GET / POST | `/api/accounts` | List, or add `{ name, type: Checking\|Savings\|CreditCard, mask: "1234" }` |
| POST | `/api/import/csv` | Multipart: `file` and `accountId`. Returns counts and any unreadable rows. |
| GET | `/api/import/batches` | Recent imports |
| DELETE | `/api/import/batches/{id}` | Undo an import |
| GET | `/api/transactions` | Current budget month, or `?from=YYYY-MM-DD&to=YYYY-MM-DD` |
| GET | `/api/transactions/inbox`, `/api/transactions/inbox/count` | Transactions that need a category |
| PATCH | `/api/transactions/{id}` | `{ categoryId, isTransfer, rulePattern? }`. Your choice locks the row. |
| GET | `/api/budget/categories` | |
| GET | `/api/budget/summary` | Every category grouped by type (limit, spent/paid, remaining, health), Left to Spend, income vs planned, last month's close and pending sweeps |
| POST / PUT | `/api/budget/categories`, `/api/budget/categories/{id}` | Add; edit with `applyTo: month|permanent`, `isHidden` |
| POST | `/api/budget/categories/order` | `{ ids }` |
| PUT | `/api/budget/months/current/income` | `{ incomeCents }` |
| POST | `/api/budget/months/{start}/close` | Close an ended month now |
| POST / DELETE | `/api/budget/sweeps/{id}/done` | Mark a sweep as sent, or undo |
| GET / POST | `/api/budget/generator`, `/api/budget/generator/apply` | Propose a budget from history; apply `{ incomeCents, limits }` |
| GET / PUT | `/api/settings/budget` | `{ sweepTarget: Debt|Savings|Split, sweepSavingsPercent }` |
| GET / DELETE | `/api/rules`, `/api/rules/{id}` | Merchant rules |
| GET | `/api/plaid/config` | Whether Plaid is set up, and the environment |
| POST | `/api/plaid/link-token` | `{ itemId? }`. Pass an item id for update mode (reconnect). |
| POST | `/api/plaid/exchange` | `{ publicToken }` from Link. Returns the bank's accounts with suggested matches. |
| POST | `/api/plaid/items/{id}/accounts` | `{ mappings: [{ plaidAccountId, action: new\|existing\|skip, accountId? }] }`, then the first sync |
| GET / DELETE | `/api/plaid/items`, `/api/plaid/items/{id}` | Connections and their status; disconnect (history is kept) |
| POST | `/api/plaid/items/{id}/reconnected` | After update-mode Link: mark active and sync |
| POST | `/api/sync` | Sync every connection now (pull to refresh) |
| POST | `/api/plaid/webhook` | Plaid only; signature-verified |
| POST | `/api/plaid/sandbox/link`, `/api/plaid/sandbox/items/{id}/refresh` | Development + sandbox only |
| GET | `/api/payday` | The next 3 checks (lines, taxes, deductions, low/likely/high, confidence), each check's payday plan, and predicted vs actual |
| PUT | `/api/payday/plan/{payDate}/{key}` | `{ done }`: check off a plan item |
| POST | `/api/payday/schedule` | Multipart `file` (.txt or .csv) and `confirm`: preview the grouped trips, or save the month (replaces it) |
| GET / PUT / DELETE | `/api/payday/schedule`, `/api/payday/schedule/{year}/{month}` | Months with trips and pay; `{ reserve, overtime, paidEntries }` |
| POST | `/api/payday/paystubs`, `/api/payday/paystubs/confirm` | Multipart PDF → parsed numbers for review (nothing saved); then save the reviewed stub |
| GET / DELETE | `/api/payday/paystubs`, `/api/payday/paystubs/{id}` | |
| GET / PUT / DELETE | `/api/payday/model` | The learned withholding model with back-tests; save edits; back to the learned fit |
| GET / PUT | `/api/payday/profile` | `{ yearsOfService, fullTime, holidayPay, flatNetPercent }` |
| GET / PUT / DELETE | `/api/payday/rules` | The pay rules table; reset to the manual |
| GET | `/api/health` | |

## Data safety

All seed data is invented. Never commit `.env`, real exports, pay stubs or schedules. `.gitignore` blocks `*.csv`, `*.pdf`, `/data/` and `/private/`.
