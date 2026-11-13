-- Data check: counts of rows that don't fit how the app works today. Read-only.
-- Run with psql against a copy of the database (see scripts/pull-data.ps1). Every line should say 0;
-- anything else is listed with enough detail to look at it (no account numbers, no tokens).
\pset pager off
\pset footer off

\echo '== Households (transactions, first, latest) =='
select left(h."Id"::text, 8) household, count(t.*) transactions, min(t."Date") first, max(t."Date") latest,
       (h."DemoUntilUtc" is not null) demo_copy
from "Households" h left join "Transactions" t on t."HouseholdId" = h."Id"
group by h."Id" order by count(t.*) desc;

\echo
\echo '== Checks (each should be 0) =='
with tx as (
  select t.*, a."Type" acct_type, a."IncludedInBudget" in_budget, c."Type" cat_type, c."IsHidden" cat_hidden, c."Name" cat_name
  from "Transactions" t
  join "Accounts" a on a."Id" = t."AccountId"
  left join "Categories" c on c."Id" = t."CategoryId"
),
checks(n, name, rows) as (
  -- Kind: 0 purchase, 1 refund, 2 income, 3 fee, 4 card payment, 5 transfer, 6 other, 7 friend's money in, 8 friend transfer to label
  -- Category type: 0 bill, 1 spending, 2 debt, 3 savings.  Source: 0 none, 1 bank, 2 rule, 3 transfer, 4 you, 5 learned, 6 best guess
  select 1, 'Marked a transfer but its kind is not card payment or transfer',
         (select count(*) from tx where "IsTransfer" and "Kind" not in (4, 5))
  union all select 2, 'Kind is card payment or transfer, not marked a transfer, filed under spending or a bill',
         (select count(*) from tx where not "IsTransfer" and "Kind" in (4, 5) and cat_type in (0, 1))
  union all select 3, 'Kind is card payment or transfer, not marked a transfer, and no category (counted nowhere)',
         (select count(*) from tx where not "IsTransfer" and "Kind" in (4, 5) and "CategoryId" is null)
  union all select 4, 'Tied to a debt but not filed under a debt line or Card interest',
         (select count(*) from tx where "DebtAccountId" is not null and coalesce(cat_type, -1) <> 2 and lower(coalesce(cat_name, '')) <> 'card interest')
  union all select 5, 'Transfer pair that is not exactly two rows',
         (select count(*) from (select "TransferGroupId" from tx where "TransferGroupId" is not null group by 1 having count(*) <> 2) g)
  union all select 6, 'Transfer pair whose two sides do not cancel out',
         (select count(*) from (select "TransferGroupId" from tx where "TransferGroupId" is not null group by 1 having count(*) = 2 and sum("AmountCents") <> 0) g)
  union all select 7, 'Friend money with no friend on it',
         (select count(*) from tx where ("IsPartnerTransfer" or "Kind" in (7, 8)) and "FriendId" is null)
  union all select 8, 'Kind is friend money but not flagged as a friend transfer',
         (select count(*) from tx where "Kind" in (7, 8) and not "IsPartnerTransfer")
  union all select 9, 'Paycheck or friend money in that also has a category (would count twice)',
         (select count(*) from tx where "Kind" in (2, 7) and "CategoryId" is not null)
  union all select 10, 'Waiting for review although you already chose or settled it',
         (select count(*) from tx where "NeedsReview" and "CategorySource" in (4, 6))
  union all select 11, 'Counts only partly (old split share that is no longer set anywhere)',
         (select count(*) from tx where "SplitFactor" <> 1)
  union all select 12, 'Old partner offset amount still on the row (no longer used)',
         (select count(*) from tx where "PartnerOffsetCents" <> 0)
  union all select 13, 'Filed under a hidden category (counts as not filed)',
         (select count(*) from tx where cat_hidden and not "IsTransfer")
  union all select 14, 'Money out with kind refund or income',
         (select count(*) from tx where "AmountCents" > 0 and "Kind" in (1, 2, 7))
  union all select 15, 'Money in with kind purchase or fee, filed under spending or a bill (acts as a refund)',
         (select count(*) from tx where "AmountCents" < 0 and "Kind" in (0, 3) and cat_type in (0, 1))
  union all select 16, 'Money in, kind purchase/fee/other, no category, not a transfer (counted nowhere)',
         (select count(*) from tx where "AmountCents" < 0 and "Kind" in (0, 3, 6) and "CategoryId" is null and not "IsTransfer")
  union all select 17, 'Interest or fee on a card still filed under a spending category',
         (select count(*) from tx where "Kind" = 3 and cat_type = 1 and acct_type = 2 and "Merchant" ilike '%interest%')
  union all select 18, 'Pending for more than 10 days',
         (select count(*) from tx where "Pending" and "Date" < current_date - 10)
  union all select 19, 'Same account, day, amount and store more than once from different sources (CSV and bank)',
         (select count(*) from (select 1 from tx group by "AccountId", "Date", "AmountCents", "Merchant" having count(*) > 1 and count(distinct "Source") > 1) d)
  union all select 20, 'Same account, day, amount and description more than once from the bank feed',
         (select count(*) from (select 1 from tx where "Source" = 2 group by "AccountId", "Date", "AmountCents", "Description" having count(*) > 1) d)
  union all select 21, 'On an account left out of the budget',
         (select count(*) from tx where not in_budget)
  union all select 22, 'Filed under a debt or savings line but it is money in on a bank account, not a transfer',
         (select count(*) from tx where cat_type in (2, 3) and "AmountCents" < 0 and acct_type <> 2 and not "IsTransfer")
  union all select 23, 'Marked a transfer with a spending or bill category (the category is ignored)',
         (select count(*) from tx where "IsTransfer" and cat_type in (0, 1))
  union all select 24, 'Merchant rules that point at a hidden category',
         (select count(*) from "CategoryRules" r join "Categories" c on c."Id" = r."CategoryId" where c."IsHidden")
  union all select 25, 'Categories with the same name in one household (not hidden)',
         (select count(*) from (select 1 from "Categories" where not "IsHidden" group by "HouseholdId", lower("Name") having count(*) > 1) d)
  union all select 26, 'Split categories (old feature, no longer used)',
         (select count(*) from "Categories" where "IsSplit")
  union all select 27, 'Fixed contributions with no friend on them',
         (select count(*) from "PartnerArrangements" where "FriendId" is null)
  union all select 28, 'Leftover-money tasks not done from a month more than 2 months old',
         (select count(*) from "SweepTasks" s join "BudgetMonths" m on m."Id" = s."BudgetMonthId" where s."DoneUtc" is null and m."EndDate" < current_date - 60)
  union all select 29, 'Debts tied to an account that is left out of the budget, not hidden',
         (select count(*) from "DebtAccounts" d join "Accounts" a on a."Id" = d."AccountId" where not a."IncludedInBudget" and not d."IsHidden")
  union all select 30, 'Savings goals tied to an account that is not a savings account',
         (select count(*) from "SavingsGoals" g join "Accounts" a on a."Id" = g."AccountId" where a."Type" <> 1)
  union all select 31, 'Budget months that overlap or leave a gap',
         (select count(*) from (select "EndDate", lead("StartDate") over (partition by "HouseholdId" order by "StartDate") next_start from "BudgetMonths") m
          where next_start is not null and next_start <> "EndDate" + 1)
  union all select 32, 'Ended more than 5 days ago but not closed',
         (select count(*) from "BudgetMonths" where "ClosedUtc" is null and "EndDate" < current_date - 6)
)
select n "#", rows "rows", name "check" from checks order by n;

\echo
\echo '== Transactions by kind, transfer flag and category type (real households only) =='
select t."Kind" kind, t."IsTransfer" transfer, coalesce(c."Type"::text, '-') category_type, t."CategorySource" source,
       case a."Type" when 0 then 'checking' when 1 then 'savings' else 'card' end account, (t."AmountCents" > 0) money_out,
       t."NeedsReview" review, count(*) n, min(t."Date") first, max(t."Date") latest
from "Transactions" t
join "Accounts" a on a."Id" = t."AccountId"
join "Households" h on h."Id" = t."HouseholdId" and h."DemoUntilUtc" is null
left join "Categories" c on c."Id" = t."CategoryId"
group by 1, 2, 3, 4, 5, 6, 7 order by 1, 2, 3, 4, 5, 6, 7;
