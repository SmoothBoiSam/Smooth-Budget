using System.Security.Cryptography;
using SmoothBudget.Core.Plaid;
using SmoothBudget.Core.Security;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

public class PlaidSyncPlannerTests
{
    private static PlaidTxn T(string id, long cents = 25_00, bool pending = false, string? pendingId = null, int day = 10) =>
        new(id, "acc", cents, new DateOnly(2026, 11, day), "COFFEE CART", null, pending, pendingId, "FOOD_AND_DRINK", "FOOD_AND_DRINK_COFFEE");

    [Fact]
    public void New_transactions_are_inserted()
    {
        var plan = PlaidSyncPlanner.Plan([], [T("a"), T("b", pending: true)], [], []);
        Assert.Equal(["a", "b"], plan.Inserts.Select(t => t.TransactionId).Order());
        Assert.Empty(plan.Updates);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void A_posted_transaction_takes_over_its_pending_row_from_an_earlier_sync()
    {
        var row = Guid.NewGuid();
        var posted = T("posted", cents: 27_50, pendingId: "pend", day: 12); // tip added when it posted

        var plan = PlaidSyncPlanner.Plan([new StoredPlaidRow(row, "pend", Pending: true)], [posted], [], ["pend"]);

        Assert.Empty(plan.Inserts);
        Assert.Empty(plan.Deletes); // the pending id's removal finds nothing: the row now belongs to the posted id
        var u = Assert.Single(plan.Updates);
        Assert.Equal((row, "posted", 27_50L, false, true), (u.RowId, u.Data.TransactionId, u.Data.AmountCents, u.Data.Pending, u.ReplacesPending));
    }

    [Fact]
    public void Removal_listed_before_the_posted_add_still_keeps_one_row()
    {
        var row = Guid.NewGuid();
        // Plaid doesn't promise an order across the added/removed lists; the planner applies adds first.
        var plan = PlaidSyncPlanner.Plan([new StoredPlaidRow(row, "pend", true)], [T("posted", pendingId: "pend")], [], ["pend"]);
        Assert.Equal(row, Assert.Single(plan.Updates).RowId);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void Pending_and_posted_in_the_same_batch_become_one_insert()
    {
        var plan = PlaidSyncPlanner.Plan([], [T("posted", pendingId: "pend"), T("pend", pending: true)], [], ["pend"]);
        var only = Assert.Single(plan.Inserts);
        Assert.Equal(("posted", false), (only.TransactionId, only.Pending));
    }

    [Fact]
    public void A_pending_transaction_that_disappears_without_posting_is_deleted()
    {
        var row = Guid.NewGuid();
        var plan = PlaidSyncPlanner.Plan([new StoredPlaidRow(row, "pend", true)], [], [], ["pend"]);
        Assert.Equal(row, Assert.Single(plan.Deletes));
    }

    [Fact]
    public void A_posted_transaction_without_a_pending_link_is_simply_new()
    {
        var row = Guid.NewGuid();
        // Some banks don't link them; Plaid then removes the pending one, so there's still only one.
        var plan = PlaidSyncPlanner.Plan([new StoredPlaidRow(row, "pend", true)], [T("posted")], [], ["pend"]);
        Assert.Single(plan.Inserts);
        Assert.Equal(row, Assert.Single(plan.Deletes));
    }

    [Fact]
    public void A_posted_row_is_never_taken_over_by_another_transaction()
    {
        var row = Guid.NewGuid();
        var plan = PlaidSyncPlanner.Plan([new StoredPlaidRow(row, "a", Pending: false)], [T("b", pendingId: "a")], [], []);
        Assert.Single(plan.Inserts);
        Assert.Empty(plan.Updates);
    }

    [Fact]
    public void Modified_and_repeated_adds_update_in_place()
    {
        var row = Guid.NewGuid();
        var plan = PlaidSyncPlanner.Plan([new StoredPlaidRow(row, "a", false)], [T("a", cents: 1_00)], [T("a", cents: 2_00)], []);
        var u = Assert.Single(plan.Updates);
        Assert.Equal((2_00L, false), (u.Data.AmountCents, u.ReplacesPending));
        Assert.Empty(plan.Inserts);
    }

    [Fact]
    public void Removing_an_unknown_id_does_nothing()
    {
        var plan = PlaidSyncPlanner.Plan([], [], [], ["nope"]);
        Assert.Empty(plan.Inserts);
        Assert.Empty(plan.Deletes);
    }
}

public class CsvOverlapMatcherTests
{
    private static PlaidTxn T(string id, int day, long cents, bool pending = false, int month = 11) =>
        new(id, "acc", cents, new DateOnly(2026, month, day), "X", null, pending, null, null, null);

    private static CsvRow Csv(int day, long cents, int month = 11) => new(Guid.NewGuid(), new DateOnly(2026, month, day), cents);

    [Fact]
    public void With_no_CSV_history_everything_is_inserted()
    {
        var d = CsvOverlapMatcher.Match([T("a", 1, 5_00)], []);
        Assert.Equal(OverlapAction.Insert, d.Single().Action);
    }

    [Fact]
    public void A_transaction_already_in_the_CSV_is_adopted_not_duplicated()
    {
        var csv = Csv(12, 64_30); // CSV posting date
        var d = CsvOverlapMatcher.Match([T("a", 10, 64_30)], [csv, Csv(20, 1_00)]); // Plaid purchase date, two days earlier

        Assert.Equal((OverlapAction.Adopt, csv.RowId), (d.Single().Action, d.Single().AdoptRowId));
    }

    [Fact]
    public void Each_CSV_row_is_adopted_at_most_once_and_the_closest_date_wins()
    {
        var early = Csv(8, 5_00);
        var close = Csv(11, 5_00);
        var d = CsvOverlapMatcher.Match([T("a", 10, 5_00), T("b", 10, 5_00), T("c", 10, 5_00)], [early, close, Csv(30, 1_00)]);

        Assert.Equal(close.RowId, d.Single(x => x.Txn.TransactionId == "a").AdoptRowId);
        Assert.Equal(early.RowId, d.Single(x => x.Txn.TransactionId == "b").AdoptRowId);
        Assert.Equal(OverlapAction.Skip, d.Single(x => x.Txn.TransactionId == "c").Action); // no third CSV coffee
    }

    [Fact]
    public void Well_after_the_last_CSV_date_everything_is_new()
    {
        var d = CsvOverlapMatcher.Match([T("a", 20 + CsvOverlapMatcher.MatchDays + 1, 5_00)], [Csv(20, 5_00)]);
        Assert.Equal(OverlapAction.Insert, d.Single().Action);
    }

    [Fact]
    public void A_purchase_from_the_CSVs_last_day_with_a_later_Plaid_date_is_still_matched()
    {
        var last = Csv(11, 36_99); // card CSV uses the purchase date
        var d = CsvOverlapMatcher.Match([T("a", 12, 36_99)], [Csv(8, 18_45), last]); // Plaid posted it on the 12th
        Assert.Equal((OverlapAction.Adopt, last.RowId), (d.Single().Action, d.Single().AdoptRowId));
    }

    [Fact]
    public void Two_same_amount_purchases_at_the_edge_match_one_and_keep_the_other()
    {
        var d = CsvOverlapMatcher.Match([T("day20", 20, 5_00), T("day21", 21, 5_00)], [Csv(20, 5_00)]);
        Assert.Equal(OverlapAction.Adopt, d.Single(x => x.Txn.TransactionId == "day20").Action);
        Assert.Equal(OverlapAction.Insert, d.Single(x => x.Txn.TransactionId == "day21").Action);
    }

    [Fact]
    public void Pending_transactions_are_always_new()
    {
        var d = CsvOverlapMatcher.Match([T("a", 10, 5_00, pending: true)], [Csv(10, 5_00), Csv(30, 1_00)]);
        Assert.Equal(OverlapAction.Insert, d.Single().Action);
    }

    [Fact]
    public void Unmatched_near_the_end_of_the_CSV_is_kept_but_older_unmatched_is_skipped()
    {
        var csv = new[] { Csv(30, 1_00) };
        var d = CsvOverlapMatcher.Match([T("recent", 27, 9_99), T("old", 1, 9_99)], csv);

        Assert.Equal(OverlapAction.Insert, d.Single(x => x.Txn.TransactionId == "recent").Action);
        Assert.Equal(OverlapAction.Skip, d.Single(x => x.Txn.TransactionId == "old").Action);
    }

    [Fact]
    public void Amounts_must_match_exactly_and_dates_within_the_window()
    {
        var d = CsvOverlapMatcher.Match([T("a", 1, 5_00), T("b", 1, 7_00)], [Csv(1, 5_01), Csv(1 + CsvOverlapMatcher.MatchDays + 1, 7_00), Csv(30, 1)]);
        Assert.All(d, x => Assert.Equal(OverlapAction.Skip, x.Action));
    }
}

public class PlaidMappingTests
{
    [Theory]
    [InlineData(false, 300_00, "LOAN_PAYMENTS", "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT", TransactionKind.CardPayment)]
    [InlineData(true, -300_00, "LOAN_PAYMENTS", "LOAN_PAYMENTS_CREDIT_CARD_PAYMENT", TransactionKind.CardPayment)]
    [InlineData(false, 100_00, "TRANSFER_OUT", "TRANSFER_OUT_SAVINGS", TransactionKind.Transfer)]
    [InlineData(false, -1_700_86, "INCOME", "INCOME_WAGES", TransactionKind.Income)]
    [InlineData(false, -800_00, "TRANSFER_IN", "TRANSFER_IN_DEPOSIT", TransactionKind.Income)]
    [InlineData(false, 12_00, "BANK_FEES", "BANK_FEES_OVERDRAFT_FEES", TransactionKind.Fee)]
    [InlineData(true, -12_00, "GENERAL_MERCHANDISE", "GENERAL_MERCHANDISE_OTHER", TransactionKind.Refund)]
    [InlineData(false, -5_00, "GENERAL_MERCHANDISE", "GENERAL_MERCHANDISE_OTHER", TransactionKind.Income)]
    [InlineData(false, -810_00, "FOOD_AND_DRINK", "FOOD_AND_DRINK_FAST_FOOD", TransactionKind.Income)] // seen in Plaid's sandbox
    [InlineData(true, 18_45, "FOOD_AND_DRINK", "FOOD_AND_DRINK_RESTAURANT", TransactionKind.Purchase)]
    [InlineData(false, 50_00, "TRANSFER_OUT", "TRANSFER_OUT_OTHER_TRANSFER_OUT", TransactionKind.Purchase)] // Zelle to a person: not an own-account move
    public void Kind_comes_from_the_category_and_direction(bool card, long cents, string primary, string detailed, TransactionKind expected)
    {
        Assert.Equal(expected, PlaidMapping.KindFor(card, cents, primary, detailed));
    }

    [Fact]
    public void Groceries_are_not_lumped_in_with_restaurants()
    {
        var ids = new Dictionary<string, Guid> { ["Groceries"] = Guid.NewGuid(), ["Eating out"] = Guid.NewGuid() };
        var grocery = PlaidMapping.BankCategoryFor("FOOD_AND_DRINK", "FOOD_AND_DRINK_GROCERIES");
        var restaurant = PlaidMapping.BankCategoryFor("FOOD_AND_DRINK", "FOOD_AND_DRINK_RESTAURANT");

        Assert.Equal(ids["Groceries"], Categorizer.Categorize("KING SOOPERS", grocery, [], ids).CategoryId);
        Assert.Equal(ids["Eating out"], Categorizer.Categorize("TAQUERIA", restaurant, [], ids).CategoryId);
    }
}

public class SecretBoxTests
{
    [Fact]
    public void Round_trips_and_never_stores_the_plaintext()
    {
        var box = new SecretBox(SecretBox.NewKey());
        const string token = "access-sandbox-00000000-1111-2222-3333-444444444444";

        var sealedText = box.Encrypt(token);

        Assert.DoesNotContain("access-sandbox", sealedText);
        Assert.Equal(token, box.Decrypt(sealedText));
        Assert.NotEqual(sealedText, box.Encrypt(token)); // fresh nonce every time
    }

    [Fact]
    public void A_wrong_key_or_tampering_fails_loudly()
    {
        var sealedText = new SecretBox(SecretBox.NewKey()).Encrypt("secret");
        Assert.ThrowsAny<CryptographicException>(() => new SecretBox(SecretBox.NewKey()).Decrypt(sealedText));

        var key = SecretBox.NewKey();
        var box = new SecretBox(key);
        var bytes = Convert.FromBase64String(box.Encrypt("secret"));
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => box.Decrypt(Convert.ToBase64String(bytes)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Bad_keys_are_rejected(string key)
    {
        Assert.Throws<ArgumentException>(() => new SecretBox(key));
    }
}
