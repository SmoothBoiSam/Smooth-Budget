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
