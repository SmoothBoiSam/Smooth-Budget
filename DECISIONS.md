# Decisions

Choices made where SPEC.md is silent. Newest at the bottom of each milestone.

## Milestone 1: Foundation

| # | Decision | Why |
|---|----------|-----|
| 1 | **Solution layout:** `backend/src/SmoothBudget.Core` (pure money math, no EF or ASP.NET), `backend/src/SmoothBudget.Api` (EF Core, Identity, endpoints), `backend/tests/*`, and `frontend/` (Vite). | Keeps the math unit-testable with no database, and the API is a thin layer over it. |
| 2 | **Money is `long` integer cents** everywhere in storage, API and Core. Fractional factors (split share) are `decimal`, and results round half away from zero to the cent. | The spec says integer cents or decimal. Cents avoid rounding drift in sums. |
| 3 | **Sign convention:** a positive `AmountCents` is money out (a purchase); a negative one is money in (refund, paycheck, transfer in). | This matches Plaid, which keeps the milestone 3 import straightforward. |
| 4 | **Safe-to-spend per day rounds down** to the cent, and is $0 when Left to Spend is zero or negative. Days remaining counts today. | Rounding down never suggests more than is actually safe. |
| 5 | **Status thresholds:** red when Left to Spend < 0; yellow when it is under 15% of total variable limits, or exactly $0; green otherwise. Exactly 15% is green. | The spec gives the 15% rule; the edges are mine. |
| 6 | **"Today" is decided in the household's time zone** (default `America/Denver`, stored on `Household`). | A server running in UTC would otherwise move to the next budget month at 5–6 pm Denver time. |
| 7 | **Budget month label** is the calendar month of its nominal start day ("November 2026" runs 11-06 to 12-06). The start day can be set from 1 to 28. | The spec calls the month "November". Days 29–31 don't exist in every month. |
| 8 | **Uncategorized purchases are not counted** in Left to Spend, but Home shows how many there are ("1 purchase needs a category and isn't counted yet"). | The spec defines the number over variable categories only and says nothing should be silently missing. The real inbox comes in milestone 2. |
| 9 | **Auth:** ASP.NET Core Identity with an HttpOnly, SameSite=Lax cookie (`Secure` outside development), 30-day sliding expiry. Passwords need 10+ characters with upper case, lower case and a digit. Accounts lock for 15 minutes after 5 failed logins. Register and login are rate-limited to 10 requests per minute per IP. | A cookie suits a same-origin PWA and keeps tokens out of JavaScript. |
| 10 | **CSRF:** relies on SameSite=Lax plus JSON-only request bodies (cross-site forms can't send `application/json` without a CORS preflight, and no CORS is enabled). | Simplest adequate protection for a same-origin app. Revisit if the API is ever called cross-origin. |
| 11 | **Household model:** every user belongs to one `Household`, and all financial data hangs off `HouseholdId`. The household id is carried as a claim in the auth cookie. | Lets the partner get a login later by adding a second user to the same household. |
| 12 | **Registration is open** (anyone who can reach the app can create an account with an empty household of their own). | Simplest for milestone 1. **Revisit before deploying publicly.** The likely choice is to disable sign-up after the first user, or invite-only. |
| 13 | **API routes are prefixed with `/api`** (`/api/auth/login`, `/api/home/summary`), and Vite proxies `/api` to the backend in development. | Keeps the frontend and API on one origin, so cookies just work. |
| 14 | **Seed data is generated relative to today's budget month**, by day offset from the month's start. Only transactions dated on or before today are created, and those dated today or yesterday are pending. The seed command resets only the demo household. | Home always shows a realistic, current number whenever you run it. |
| 15 | **Local secrets** live in the git-ignored `.env` at the repo root, loaded by DotNetEnv at startup (real environment variables win). CI and hosting use plain environment variables. | Follows CLAUDE.md. |
| 16 | **Migrations run on startup in Development only** (`Database:MigrateOnStartup` overrides this). The seed command always migrates first. | Convenient locally without silently migrating production. |
| 17 | **API tests run against real PostgreSQL**: each test run creates a throwaway `smoothbudget_test_<guid>` database and drops it afterwards. CI provides Postgres as a service container. | Tests the real provider, query translation and migrations instead of an in-memory fake. |
| 18 | **Home omits "next payday" and "upcoming bills"** for now. | They depend on the pay engine (milestone 9) and recurring detection (milestone 6). The spec's milestone 1 "done when" covers Left to Spend only. |
| 19 | **PWA:** only a web manifest and mobile meta tags for now. The service worker and offline cache come in milestone 10. | Matches the build plan. |
| 20 | **Bottom nav** shows Home, Budget, Debt, Savings, Payday, More (Bills, Partner and Settings go under More, as the spec allows). Only Home is active. | Seven tabs are too cramped at 375 px. |

## Milestone 2: Data in (CSV import, rules, transfers, inbox)

| # | Decision | Why |
|---|----------|-----|
| 21 | **CSV formats are detected from the header row:** Chase checking/savings (`Details, Posting Date, …, Balance`), Chase credit (`Transaction Date, Post Date, …, Memo`) and Apple Card (`…, Clearing Date, …, Amount (USD), Purchased By`). Columns are matched by name, so extra or reordered columns and Chase's trailing comma are fine. | Tolerant of small export changes. **The layouts come from the banks' standard exports, not from your files.** Check with a real export before relying on it. |
| 22 | **Which date is used:** posting date for Chase checking (it's the only one), and transaction (purchase) date for both cards. | SPEC section 3: "A card purchase counts on the purchase date." |
| 23 | **Sign conversion:** Chase checking and Chase credit amounts are flipped (purchases are negative in those exports); Apple Card amounts are kept (purchases are positive). | Matches the app's convention (decision 3). |
| 24 | **Columns dropped on import:** Apple's "Purchased By" (a person's name), Chase's running Balance, Memo and check number. Any run of 8+ digits in a description is masked to its last four (`••8901`). Account "last 4" accepts exactly four digits. | CLAUDE.md: never store account numbers or other people's names. |
| 25 | **Duplicate detection:** bank CSVs have no transaction ids, so a row's fingerprint is a SHA-256 of date, amount and description. On import, for each fingerprint we only add as many rows as the file has beyond what the account already holds. | Re-importing an overlapping export never duplicates anything, and two identical coffees on the same day are both kept. |
| 26 | **Transfer detection, two passes:** (1) single-row signals: a card's own "Payment" row, Chase `LOAN_PMT` and `ACCT_XFER`, and known descriptions ("Payment to Chase card", "CHASE CREDIT CRD AUTOPAY", "APPLECARD GSBANK", "Online Transfer to/from"); (2) pairing: the same amount out of one account and into another of yours within 5 days, where at least one side already looks like a payment or transfer. Bare "AUTOPAY" is not a signal because utilities use it. | Catches payments whose bank-side description is unknown, without ever pairing an ordinary purchase with a refund. |
| 27 | **The checking side of a card payment goes to the first Debt payment category; the card side has no category.** A move to a savings account goes to the first Savings transfer category when the savings side is imported. All of these are `IsTransfer` and never count as spending. | SPEC section 3: the card payment counts as a debt payment, not new spending. |
| 28 | **Categorization precedence:** your own choice > merchant rule > the bank's category. The bank's category is mapped by a small built-in table ("Food & Drink" and "Restaurants" → Eating out, "Grocery" → Groceries, …) or by an exact name match. Chase checking has no category column, so its purchases go to the inbox until a rule matches. | SPEC section 6.1 step 1, read as layers where later ones win. |
| 29 | **Merchant rules match "merchant contains pattern"**, case-insensitive; the longest pattern wins, then the newest. The pattern starts as the cleaned merchant name and is editable (shorten "KING SOOPERS DENVER CO" to "KING SOOPERS" to match every store). | Simple, predictable, and explainable. |
| 30 | **A new rule also applies to existing transactions** that only had a bank category or none. It never changes transactions you set by hand or transfers. Deleting a rule leaves past transactions as they are. | The spec says "future transactions"; clearing matching inbox items too is what you'd expect after creating a rule. |
| 31 | **Your edits lock a transaction:** after you set a category or the transfer flag, rules and transfer detection never change it again. | Your corrections always win. |
| 32 | **The inbox covers all time**, newest first, and holds anything that affects spending but has no category: purchases, fees and refunds. Income, card payments and transfers never appear. Home's line counts only the current budget month. | Nothing is silently missing. Refunds are included because they reduce a category's spending. |
| 33 | **Every upload is an import batch that can be undone** (deletes exactly the rows it added). Undo doesn't revert transfer flags already set on rows from other imports. | A safe way to back out a wrong file or account. |
| 34 | **Upload limit 5 MB; multipart uploads skip ASP.NET's antiforgery token.** The SameSite=Lax auth cookie isn't sent on cross-site POSTs, so a forged upload arrives unauthenticated. | Same reasoning as decision 10. |
| 35 | **Navigation uses the URL hash** (`#/`, `#/budget`, `#/more`) instead of a router library. | Three screens don't need a dependency. |

## Budget tab: per-category view (requested after milestone 2)

| # | Decision | Why |
|---|----------|-----|
| 36 | **The Budget tab shows categories, not a ledger.** It's grouped as Spending, Bills & subscriptions, Debt payments and Savings. Each category shows its limit, spent or paid, remaining or over, and a progress bar, and you can tap it to see its transactions. The ledger moved to "All transactions this month", and the inbox has its own view. | You asked for it, and it's SPEC section 4.3. Pulled forward from milestone 4; budget editing, month close and rollover are still milestone 4. |
| 37 | **Only Spending (variable) categories make up Left to Spend.** Bills & subscriptions, debt payments and savings are shown as "paid / to go" and are labeled "Not part of Left to Spend". Their remaining amounts plus the carried-over deficit add up to Left to Spend exactly, and a test checks it. | SPEC section 3 formula. |
| 38 | **Debt-payment and savings categories count transfers toward "paid".** A card payment from checking, or a move to savings, is how those get paid. Spending and bill categories never count transfers. | Otherwise "Credit card payment" would always show $0 paid. |
| 39 | **Category health:** spending categories are red when over, amber with under 15% left, and green otherwise (the same 15% rule as Home). Bills, debt and savings only show "over" or not, because a fully paid bill is good, not "low". | Consistent with decision 5. |
| 40 | **Home and Budget load their data through one shared loader** (`CurrentMonthLoader`), so the two screens can't disagree. | One source of truth. |
| 41 | **Known gap:** a subscription the bank labels as a spending category (e.g. a streaming service under "Entertainment") counts against that spending category until you move it. For now, set its category to Subscriptions with "Always use this category". Automatic recurring detection comes in milestone 6. | Bank categories don't distinguish subscriptions. |

## Checked against your real exports (Nov 2026)

The three real exports (Chase checking, Chase credit, Apple Card; about a year, 1,345 rows) were dry-run locally through the Core parser and transfer detector. Nothing was imported or committed. Results: 0 parse errors; 32 of 33 card-side payments matched to their checking debit, and the one left over (10/31/2026) is after the checking export ends. It's still treated as a payment.

| # | Decision | Why |
|---|----------|-----|
| 42 | **Store card payments count as card payments** (`STORECRD_PMT`, e.g. Amazon's store card). | They appeared in the real checking export and would otherwise have looked like purchases. |
| 43 | **ACH boilerplate is stripped from merchant names** ("WEB ID: …", "PPD ID: …" and the rest of the line), so "MAPLE COURT RENT WEB ID" becomes "MAPLE COURT RENT". Fingerprints use the description, so duplicate detection is unaffected. | Cleaner names and rule patterns. |
| 44 | **Zelle and Venmo descriptions keep the other person's display name** (e.g. "ZELLE PAYMENT TO …"). | The partner's Zelle name is needed to match contributions (SPEC Appendix D, milestone 7). CLAUDE.md's no-names rule covers pay stubs and schedules. |
