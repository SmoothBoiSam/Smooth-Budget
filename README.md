# Smooth Budget

A phone-first budgeting app whose Home screen answers one question: **how much do I have left to spend this month?**
It's built around a monthly flight schedule and paychecks, bank sync through Plaid, and an automatic budget that follows your pay.

The full spec (`SPEC.md`) stays out of the repo because its appendices contain real pay figures. [DECISIONS.md](DECISIONS.md) records every choice made where the spec is silent, numbered; code comments cite those numbers ("DECISIONS.md 308").

All ten milestones in the spec are done. Work since then is follow-ups and fixes, each one recorded in DECISIONS.md.

## What it does

The app has five tabs.

- **Home:**
  - **Left to Spend** for the budget month, with a ring and a cat that shows at a glance how much is left.
  - **Safe to Spend** when checking might not keep up before the next payday.
  - The month overview, in numbers: income in vs expected, bills, spending, debt and savings.
  - The next payday and the next bills.
  - Notices for anything that needs you, like a bank to reconnect or a friend's late payment.
  - Pull to refresh, and **Update bank data**, which asks Plaid to check every bank now, within monthly limits.
- **Budget:**
  - Spending categories, bills (one line each), debt payments and savings, each with spent vs limit and a "Maxed" state.
  - The **automatic budget** proposes limits from your history and your expected pay. It squeezes the least important spending first when a month brings in less. Limits you set yourself stay.
  - Budget settings: **Extra money**, one savings/debt split for what's left each month after bills, spending and card minimums (and for anything unspent when a month closes); income; archived and unused categories; and **Refresh categories from bank**. The demo leaves that last one out.
- **Tasks:**
  - **Review transactions:** file anything the app couldn't categorize. One card covers spending, bills, income and transfers, and can make a merchant rule.
  - **Pay period tasks:** for each paycheck, what to keep in checking for bills, what to pay the cards, and what to move to savings.
    - Tasks only check themselves off, from what your accounts show; there's nothing to tick by hand. Moves to the same account merge into one.
    - Each task shows how far along it is: how far over or short an account is for its bills, or how much of a move has been sent and how much is left, with a bar.
    - A later check in the month sends what the earlier ones left of the month's card payment and savings, not just an even share.
    - They never ask for more than checking can spare right now. The rest waits for a bonus still to come, or for room in checking, and the task says when.
  - **Upload** a new schedule or pay stub.
- **Money:**
  - **Paychecks:** import the monthly SkedPlus+ schedule (the detailed text export, or the older CSV) and past pay stub PDFs. Stubs are reviewed before saving, and the app learns your taxes and deductions from them.
    - You get the next 3 checks with a low/likely/high range, predicted vs actual once a check lands, and editable pay rules.
    - A pay period starts when its paycheck actually lands.
  - **Income:** past and expected income, including bonuses you expect.
  - **Debit:** checking and cash.
  - **Debt:** cards followed from their accounts and loans by hand, APR and minimums from the bank (Plaid Liabilities), and expected interest.
    - The payoff engine uses Avalanche by default and compares Snowball. It shows a debt-free date and a payoff chart under a line at your cards' total credit limit. **Payment settings** picks Avalanche or Snowball and shows the extra each month, the debt side of the split.
  - **Savings:** balances, counting transfers still on their way in. Goals with projected dates from their account's share of the month's savings, a rainy-day fund, and a 12-month chart. **Payment settings** shows the savings side of the split and divides it between your savings accounts by percent, and the pay period tasks send each its part.
  - **Transactions:** every transaction, with search.
- **More:**
  - **Accounts:** every account by kind, with where its numbers come from (its bank, uploads, or you), its balance, and anything it needs from you. Add one: connect a bank through Plaid, add one you upload CSVs for (Chase checking, Chase credit, Apple Card), or track one yourself with a balance you update. A new card asks what it owes today; its later charges and payments count from there. Bank connections and uploaded files are here too.
    - Each account has its own page: a **nickname** used everywhere in place of its bank name and last four digits, its kind (checking and savings can swap, and its transfers are filed again), the balance of one the bank doesn't sync, when it last synced, its uploads with undo, its transactions, its savings share and goals or a card's APR, minimum and due day, and **Count in budget**.
    - Disconnecting a bank asks whether to keep its accounts as closed history, keep tracking them, or delete them.
    - **Close** an account and it keeps its history (past months, transfers, bills) but leaves balances, Debt, Savings and the pay period tasks. **Delete** also deletes its transactions. Either way its savings goals, its savings share and any leftover move go to the account you pick, and bills paid from it follow. **Reopen** brings a closed one back.
  - **Sharing:** **Friends**.
    - Friends are people you share costs with. They can have their own linked account or none, and each friend has a settle-up balance. A fixed contribution toward a bill, like part of the rent, is matched to their transfers.
  - Appearance (System, Light or Dark) and Notifications (Web Push: alerts when something happens, and reminders for paydays and due dates that wait for daytime).

Also:
- It installs to a phone's home screen.
- When offline, it opens to the last known numbers.
- Sign-up is invite-only through a friend's link once an account exists.
- **Try the demo** on the sign-in screen gives each visitor a fresh copy of made-up data, with bank sync and notifications off.

## Stack

- **Backend:** ASP.NET Core (.NET 10) minimal API, EF Core, ASP.NET Core Identity (cookie auth), PostgreSQL 17
- **Frontend:** React 19 + TypeScript, Vite, Tailwind CSS v4
- **Money math:** `backend/src/SmoothBudget.Core`, pure C# with unit tests. The frontend only displays results.

```
backend/src/SmoothBudget.Core        Budget-month calendar, Left to Spend, budget generator, payoff engine, pay engine,
                                     CSV parsing, merchant cleanup, dedup, categorizer, transfer detector
backend/src/SmoothBudget.Api         EF Core model + migrations, auth, endpoints (one folder per area), Plaid,
                                     import pipeline, notifications, demo seeder
backend/tests/SmoothBudget.Core.Tests    Unit tests for the money math and import logic
backend/tests/SmoothBudget.Api.Tests     End-to-end API tests against a throwaway Postgres DB
frontend/                            Vite React app (one file per screen in frontend/src)
scripts/                             Seed fake data, move or pull hosted data (see DEPLOY.md)
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
2. In the app, open **More → Your accounts → Upload a CSV**, add the account once (name, type, optionally the last 4 digits, and for a card what it owes today), pick the file, and import.
3. Card payments and transfers are detected automatically. Anything without a category shows up under **Budget → Needs a category**. Tap a transaction to set its category, and tick "Always use this category" to create a merchant rule.

Re-importing an overlapping export is safe: rows already in the account are skipped, including ones your bank already synced through Plaid (in either order). Each import can be undone from **Recent imports**.

### Connecting banks with Plaid

1. Put your Plaid keys in `.env`: `PLAID_CLIENT_ID`, `PLAID_SECRET`, `PLAID_ENV=sandbox`, and a `PLAID_TOKEN_KEY` (see `.env.example` for the one-line command that generates it).
2. Open **More → Your accounts → Bank connections**:
   - **Connect a bank** opens Plaid Link. In sandbox, sign in with Plaid's test login `user_good` / `pass_good`.
   - **Add Plaid's sandbox test bank (dev only)** links Plaid's test bank without the Link screen.
3. Choose, for each account at the bank, **Import as a new account**, **Same as …** (pick the account you already imported CSVs into, so nothing is counted twice), or **Don't import**.
4. Pull down on Home (or tap **Update bank data**, or **Sync now**) to sync. A background sweep also syncs every 6 hours.

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

Everything is under `/api`, signed in with the auth cookie (except sign-in, the demo, the Plaid webhook and `/api/health`).
Each area maps its own endpoints in its folder under `backend/src/SmoothBudget.Api`:

| Area | Where | Covers |
|---|---|---|
| Auth | `Auth/AuthEndpoints.cs` | Sign up (with a friend's invite), sign in and out, `me`, the demo (`POST /api/auth/demo`) |
| Home | `Program.cs`, `Home/` | `GET /api/home/summary`: Left to Spend, Safe to Spend, the month overview, notices, sync health |
| Accounts | `Accounts/` | Accounts and balances, Debit, nicknames, closing, reopening and deleting an account |
| Transactions | `Transactions/TransactionEndpoints.cs` | Search, Review, filing a transaction, merchant rules, income |
| Budget | `Budget/BudgetEndpoints.cs` | Categories, the summary, the automatic budget and generator, month close and sweeps, settings |
| Bills | `Bills/RecurringEndpoints.cs` | Bills and subscriptions found from your transactions |
| Debt | `Debt/DebtEndpoints.cs` | Cards and loans, the payoff plan, interest |
| Savings | `Savings/` | Savings accounts, goals, the split between accounts |
| Payday | `Payday/PaydayEndpoints.cs` | Schedules, pay stubs, the withholding model, pay rules, expected income, the next checks and their tasks |
| Friends | `Shared/SharedEndpoints.cs` | Friends, invites, shared costs and settle-up |
| Import | `Import/ImportEndpoints.cs` | CSV import and undo |
| Plaid | `Plaid/PlaidEndpoints.cs` | Link, connections, sync, Update bank data, the verified webhook, sandbox helpers (dev only) |
| Notifications | `Notifications/Notifications.cs` | Web Push subscriptions and settings |

## Hosting

See [DEPLOY.md](DEPLOY.md): one Docker container on Render with a managed Postgres database, deployed on every push, and `scripts/move-data.ps1` to move your local data across.

## Data safety

All seed data is invented. Never commit `.env`, real exports, pay stubs or schedules. `.gitignore` blocks `*.csv`, `*.pdf`, `/data/` and `/private/`.
