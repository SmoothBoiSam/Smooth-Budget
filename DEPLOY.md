# Hosting Smooth Budget on Render (with your current data)

The whole app is one Docker container: the API serves the website from the same address, so sign-in cookies just work. Render builds it from this repo, runs it with a managed Postgres database, and redeploys whenever you push to GitHub. `render.yaml` describes both; `scripts/move-data.ps1` copies your local data across.

You do the steps that need your accounts and keys. Nothing secret goes into the repo: secrets are typed into Render's dashboard.

**Cost:** an always-on web service and a small Postgres database, on paid plans. Free plans put the app to sleep, which stops background bank syncs and Plaid webhooks. Check Render's current pricing before you start; the plans are set in `render.yaml` (`starter` and `basic-256mb`) and can be changed in the dashboard.

## 1. Put the code in a private GitHub repo

1. On github.com, create a **private** repository named `smooth-budget` (no README, no .gitignore: the repo already has them).
2. From the repo folder on your computer:

```bash
git remote add origin https://github.com/YOUR-USERNAME/smooth-budget.git
```

```bash
git push -u origin main
```

The push runs CI (tests, lint, and a Docker build) on GitHub. `.gitignore` keeps `.env`, `private/`, PDFs and CSVs out of it.

## 2. Create the app on Render

1. Sign up at render.com and connect your GitHub account (allow access to the `smooth-budget` repo only).
2. **New → Blueprint**, pick the repo. Render reads `render.yaml` and shows a database (`smoothbudget-db`) and a web service (`smoothbudget`).
3. It asks for the secret values. Copy them from your local `.env`:
   - `PLAID_CLIENT_ID` and `PLAID_SECRET`: your Plaid keys.
   - `PLAID_TOKEN_KEY`: **exactly the same value as in `.env`.** Your linked banks' tokens are encrypted with it; a different key means re-linking every bank.
   - `PLAID_WEBHOOK_URL`: leave empty for now (step 5).
4. **Apply.** The first deploy takes a few minutes. When it's live, open `https://YOUR-APP.onrender.com/api/health`: it should say `{"status":"ok"}`.

The app creates its own tables on first start (`Database__MigrateOnStartup`, set in the `Dockerfile`).

## 3. Don't sign up on the hosted app

Sign-up is closed in production once any account exists. Until step 4 the hosted database is empty, so the sign-in screen would still offer "Create an account". Skip it: your account arrives with your data.

## 4. Move your data

1. In Render, open the database → **Connect** → copy the **External Database URL**.
2. On your computer, from the repo folder (PowerShell 7):

```bash
pwsh scripts/move-data.ps1
```

   It asks for that URL and your sign-in email, then:
   - dumps your local database to the git-ignored `private/move` folder;
   - restores it into the hosted database (replacing what's there);
   - removes every login except yours (the demo and test logins; it removes none if your email isn't found);
   - deletes the dump file (it holds your real data) unless you ask to keep it.
3. In Render, open the web service → **Manual Deploy → Restart service**, so it picks up the restored sign-in keys.
4. Open `https://YOUR-APP.onrender.com` and sign in with your usual email and password.

The fake demo and test data that came along can't be reached by anyone: its logins are gone.

## 5. Turn on Plaid webhooks

In Render, web service → **Environment**, set `PLAID_WEBHOOK_URL` to `https://YOUR-APP.onrender.com/api/plaid/webhook` and save (it redeploys). On startup the app tells Plaid to send each linked bank's updates there, so new transactions arrive on their own instead of only when you pull to refresh (the 6-hourly background sync still runs too).

Connecting a *new* bank works best from a computer's browser: banks like Chase sign you in through a pop-up window, which phone browsers handle poorly.

## 6. Install it on your phone

Open `https://YOUR-APP.onrender.com` on your phone and sign in.

- **iPhone (Safari):** Share → **Add to Home Screen**.
- **Android (Chrome):** menu → **Install app**.

It opens full-screen from its icon, and opens to your last Left to Spend when you're offline.

## Making changes after it's hosted

Work locally exactly as now (your local database stays separate), then:

```bash
git push
```

CI runs the tests, and Render rebuilds and redeploys in a few minutes. Database changes (migrations) apply automatically when the new version starts. Your phone picks up the new version the next time you open the app.

## Good to know

- **Backups:** check your Render database plan's backup and recovery options, and turn on whatever it offers.
- **Your own domain:** Render → web service → Settings → Custom Domains. If you add one, update `PLAID_WEBHOOK_URL` to match.
- **Logs:** Render → web service → Logs. Plaid tokens and full account numbers are never logged.
- **Rotating `PLAID_TOKEN_KEY`:** don't. Changing it makes the stored bank tokens unreadable, and every bank needs re-linking.
