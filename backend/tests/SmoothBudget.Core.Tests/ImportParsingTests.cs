using SmoothBudget.Core.Import;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Tests;

// All CSV content in these tests is invented. Layouts follow the banks' public export formats.
public class CsvReaderTests
{
    [Fact]
    public void Handles_quotes_embedded_commas_doubled_quotes_and_CRLF()
    {
        var rows = CsvReader.Parse("a,b,c\r\n\"x, y\",\"say \"\"hi\"\"\",3\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["x, y", "say \"hi\"", "3"], rows[1]);
    }

    [Fact]
    public void Skips_blank_lines_strips_BOM_and_keeps_ragged_rows()
    {
        var rows = CsvReader.Parse("﻿h1,h2\n\n1,2,\n   \n3,4");
        Assert.Equal(3, rows.Count);
        Assert.Equal("h1", rows[0][0]);
        Assert.Equal(3, rows[1].Length); // Chase's trailing comma
    }

    [Fact]
    public void Newline_inside_quotes_stays_in_the_field()
    {
        var rows = CsvReader.Parse("a\n\"line1\nline2\"\n");
        Assert.Equal("line1\nline2", rows[1][0]);
    }
}

public class MerchantTextTests
{
    [Theory]
    [InlineData("Card Purchase 11/09 King Soopers #0012 Denver CO Card 1234", "KING SOOPERS DENVER CO")]
    [InlineData("RECURRING CARD PURCHASE 11/01 STREAMCO 800-555-0100 CA CARD 1234", "STREAMCO CA")]
    [InlineData("SQ *BLUE DOOR COFFEE", "BLUE DOOR COFFEE")]
    [InlineData("TST* NOODLE HOUSE 123", "NOODLE HOUSE")]
    [InlineData("FUEL STOP #123", "FUEL STOP")]
    [InlineData("  taqueria   uno ", "TAQUERIA UNO")]
    [InlineData("7-ELEVEN", "7-ELEVEN")] // nothing left after stripping: fall back to the whole text
    [InlineData("MAPLE COURT RENT WEB ID: ••6789", "MAPLE COURT RENT")] // ACH boilerplate (seen in real exports)
    [InlineData("PAYROLL DEMO AIR PPD ID: ••7890", "PAYROLL DEMO AIR")]
    [InlineData("APPLECARD GSBANK PAYMENT ••8901 WEB ID: ••9999", "APPLECARD GSBANK PAYMENT")]
    [InlineData("MAPLE COURT APTS WEB PMTS", "MAPLE COURT APTS WEB PMTS")] // "WEB" alone is not boilerplate
    public void Normalize_produces_a_stable_merchant_key(string input, string expected)
    {
        Assert.Equal(expected, MerchantText.Normalize(input));
    }

    [Fact]
    public void Sanitize_masks_long_digit_runs_that_could_be_account_numbers()
    {
        var s = MerchantText.Sanitize("APPLECARD GSBANK PAYMENT 12345678901 WEB ID: 9999999999");
        Assert.DoesNotContain("12345678901", s);
        Assert.DoesNotContain("9999999999", s);
        Assert.Contains("••8901", s);
        Assert.Equal("Card 1234 on 11/09", MerchantText.Sanitize("Card  1234 on 11/09")); // short numbers untouched
    }
}

public class BankCsvParserTests
{
    private const string ChaseDebit = """
        Details,Posting Date,Description,Amount,Type,Balance,Check or Slip #
        CREDIT,11/06/2026,"PAYROLL DEMO AIR PPD ID: 1234567890",1700.86,ACH_CREDIT,2900.86,,
        DEBIT,11/10/2026,"Card Purchase 11/09 King Soopers #0012 Denver CO Card 1234",-64.30,DEBIT_CARD,2873.56,,
        CREDIT,11/12/2026,"Card Purchase Return 11/11 King Soopers #0012 Denver CO Card 1234",5.00,DEBIT_CARD,2878.56,,
        DEBIT,11/14/2026,"Payment to Chase card ending in 4321 11/14",-300.00,LOAN_PMT,2578.56,,
        CREDIT,11/20/2026,"Zelle payment from PARTNER DEMO 24681357",800.00,PARTNERFI_TO_CHASE,3378.56,,
        DEBIT,11/21/2026,"Online Transfer to SAV ...9876 transaction#: 1234567890 11/21",-100.00,ACCT_XFER,3278.56,,
        DEBIT,11/22/2026,"MONTHLY SERVICE FEE",-12.00,FEE_TRANSACTION,3266.56,,
        """;

    private const string ChaseCredit = """
        Transaction Date,Post Date,Description,Category,Type,Amount,Memo
        11/08/2026,11/09/2026,TAQUERIA UNO,Food & Drink,Sale,-18.45,
        11/13/2026,11/14/2026,BIG BOX STORE,Shopping,Return,12.00,
        11/14/2026,11/14/2026,Payment Thank You-Mobile,,Payment,300.00,
        11/15/2026,11/15/2026,ANNUAL MEMBERSHIP FEE,Fees & Adjustments,Fee,-95.00,
        """;

    private const string Apple = """
        Transaction Date,Clearing Date,Description,Merchant,Category,Type,Amount (USD),Purchased By
        11/10/2026,11/11/2026,"FRESH MARKET 0042 DENVER CO",Fresh Market,Grocery,Purchase,91.45,Demo Person
        11/15/2026,11/15/2026,"ACH DEPOSIT INTERNET TRANSFER FROM ACCOUNT ENDING IN 1234",Payment,Payment,Payment,-150.00,Demo Person
        11/16/2026,11/16/2026,"BIG BOX STORE RETURN",Big Box Store,Shopping,Credit,-20.00,Demo Person
        11/30/2026,11/30/2026,"MONTHLY INSTALLMENT",Apple Online Store,Shopping,Installment,41.58,Demo Person
        """;

    [Fact]
    public void Detects_each_format_from_its_header()
    {
        Assert.Equal(BankCsvFormat.ChaseDebit, BankCsvParser.Parse(ChaseDebit).Format);
        Assert.Equal(BankCsvFormat.ChaseCredit, BankCsvParser.Parse(ChaseCredit).Format);
        Assert.Equal(BankCsvFormat.AppleCard, BankCsvParser.Parse(Apple).Format);
    }

    [Fact]
    public void Unknown_header_is_rejected_with_a_helpful_message()
    {
        var r = BankCsvParser.Parse("Date,Thing,Value\n11/01/2026,x,1.00");
        Assert.Equal(BankCsvFormat.Unknown, r.Format);
        Assert.Empty(r.Rows);
        Assert.Contains("Unrecognized", r.Errors.Single().Message);
    }

    [Fact]
    public void Chase_debit_rows_flip_sign_and_get_the_right_kind()
    {
        var r = BankCsvParser.Parse(ChaseDebit);
        Assert.Empty(r.Errors);
        Assert.Equal(7, r.Rows.Count);

        Assert.Equal((-1_700_86L, TransactionKind.Income), (r.Rows[0].AmountCents, r.Rows[0].Kind));
        Assert.Equal((64_30L, TransactionKind.Purchase), (r.Rows[1].AmountCents, r.Rows[1].Kind));
        Assert.Equal((-5_00L, TransactionKind.Refund), (r.Rows[2].AmountCents, r.Rows[2].Kind));
        Assert.Equal((300_00L, TransactionKind.CardPayment), (r.Rows[3].AmountCents, r.Rows[3].Kind));
        Assert.Equal((-800_00L, TransactionKind.Income), (r.Rows[4].AmountCents, r.Rows[4].Kind));
        Assert.Equal((100_00L, TransactionKind.Transfer), (r.Rows[5].AmountCents, r.Rows[5].Kind));
        Assert.Equal((12_00L, TransactionKind.Fee), (r.Rows[6].AmountCents, r.Rows[6].Kind));
        Assert.Equal(new DateOnly(2026, 11, 10), r.Rows[1].Date); // posting date
        Assert.Equal("KING SOOPERS DENVER CO", r.Rows[1].Merchant);
        Assert.Null(r.Rows[1].BankCategory);
        Assert.DoesNotContain("1234567890", r.Rows[0].Description);
    }

    [Fact]
    public void Chase_credit_uses_the_purchase_date_and_flips_sign()
    {
        var r = BankCsvParser.Parse(ChaseCredit);
        Assert.Empty(r.Errors);
        Assert.Equal((new DateOnly(2026, 11, 8), 18_45L, TransactionKind.Purchase, "Food & Drink"),
            (r.Rows[0].Date, r.Rows[0].AmountCents, r.Rows[0].Kind, r.Rows[0].BankCategory));
        Assert.Equal((-12_00L, TransactionKind.Refund), (r.Rows[1].AmountCents, r.Rows[1].Kind));
        Assert.Equal((-300_00L, TransactionKind.CardPayment), (r.Rows[2].AmountCents, r.Rows[2].Kind));
        Assert.Equal((95_00L, TransactionKind.Fee), (r.Rows[3].AmountCents, r.Rows[3].Kind));
    }

    [Fact]
    public void Apple_card_keeps_sign_uses_the_merchant_column_and_never_reads_purchased_by()
    {
        var r = BankCsvParser.Parse(Apple);
        Assert.Empty(r.Errors);
        Assert.Equal((91_45L, TransactionKind.Purchase, "FRESH MARKET", "Grocery"),
            (r.Rows[0].AmountCents, r.Rows[0].Kind, r.Rows[0].Merchant, r.Rows[0].BankCategory));
        Assert.Equal((-150_00L, TransactionKind.CardPayment), (r.Rows[1].AmountCents, r.Rows[1].Kind));
        Assert.Null(r.Rows[1].BankCategory); // Apple writes "Payment" as the category; not a budget category
        Assert.Equal((-20_00L, TransactionKind.Refund), (r.Rows[2].AmountCents, r.Rows[2].Kind));
        Assert.Equal(TransactionKind.Purchase, r.Rows[3].Kind); // installments are purchases
        Assert.All(r.Rows, row => Assert.DoesNotContain("Demo Person", row.Description + row.Merchant));
    }

    [Fact]
    public void Bad_rows_are_reported_with_their_line_number_and_good_rows_still_parse()
    {
        var r = BankCsvParser.Parse("""
            Transaction Date,Post Date,Description,Category,Type,Amount,Memo
            11/08/2026,11/09/2026,GOOD,Shopping,Sale,-1.00,
            13/45/2026,11/09/2026,BAD DATE,Shopping,Sale,-1.00,
            11/08/2026,11/09/2026,BAD AMOUNT,Shopping,Sale,abc,
            11/08/2026,11/09/2026,,Shopping,Sale,-1.00,
            """);
        Assert.Single(r.Rows);
        Assert.Equal([3, 4, 5], r.Errors.Select(e => e.RowNumber));
    }

    [Theory]
    [InlineData("-1,234.56", -123456)]
    [InlineData("$12.30", 1230)]
    [InlineData("(12.30)", -1230)]
    [InlineData("7", 700)]
    [InlineData("0.1", 10)]
    public void Amounts_parse_to_exact_cents(string input, long expected)
    {
        Assert.Equal(expected, BankCsvParser.ParseCents(input, "Amount"));
    }

    [Fact]
    public void Fractions_of_a_cent_are_rejected()
    {
        Assert.Throws<FormatException>(() => BankCsvParser.ParseCents("1.005", "Amount"));
    }
}

public class ImportDeduplicatorTests
{
    private static ParsedRow Row(int day, long cents, string desc) =>
        new(day, new DateOnly(2026, 11, day), cents, desc, desc, null, null, TransactionKind.Purchase);

    [Fact]
    public void Reimporting_the_same_file_adds_nothing()
    {
        var rows = new[] { Row(1, 5_00, "COFFEE"), Row(2, 9_00, "LUNCH") };
        var existing = rows.GroupBy(r => r.Fingerprint).ToDictionary(g => g.Key, g => g.Count());

        var (fresh, dupes) = ImportDeduplicator.SelectNew(rows, existing);

        Assert.Empty(fresh);
        Assert.Equal(2, dupes);
    }

    [Fact]
    public void Two_identical_purchases_on_the_same_day_are_both_kept()
    {
        var (fresh, dupes) = ImportDeduplicator.SelectNew([Row(1, 5_00, "COFFEE"), Row(1, 5_00, "COFFEE")], new Dictionary<string, int>());
        Assert.Equal(2, fresh.Count);
        Assert.Equal(0, dupes);
    }

    [Fact]
    public void An_overlapping_export_only_adds_what_is_new_including_a_third_identical_coffee()
    {
        var first = new[] { Row(1, 5_00, "COFFEE"), Row(1, 5_00, "COFFEE"), Row(2, 9_00, "LUNCH") };
        var existing = first.GroupBy(r => r.Fingerprint).ToDictionary(g => g.Key, g => g.Count());
        var second = new[] { Row(1, 5_00, "COFFEE"), Row(1, 5_00, "COFFEE"), Row(1, 5_00, "COFFEE"), Row(2, 9_00, "LUNCH"), Row(3, 20_00, "GAS") };

        var (fresh, dupes) = ImportDeduplicator.SelectNew(second, existing);

        Assert.Equal(["COFFEE", "GAS"], fresh.Select(r => r.Description));
        Assert.Equal(3, dupes);
    }
}
