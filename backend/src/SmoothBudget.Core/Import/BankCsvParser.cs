using System.Globalization;
using SmoothBudget.Core.Transactions;

namespace SmoothBudget.Core.Import;

public enum BankCsvFormat
{
    Unknown = 0,
    /// <summary>Chase checking or savings: Details, Posting Date, Description, Amount, Type, Balance, Check or Slip #.</summary>
    ChaseDebit = 1,
    /// <summary>Chase credit card: Transaction Date, Post Date, Description, Category, Type, Amount, Memo.</summary>
    ChaseCredit = 2,
    /// <summary>Apple Card: Transaction Date, Clearing Date, Description, Merchant, Category, Type, Amount (USD), Purchased By.</summary>
    AppleCard = 3,
}

/// <summary>
/// One row, already converted to the app's conventions: positive cents = money out.
/// Columns the app must not keep (Apple's "Purchased By", running balances, memos) are dropped here.
/// </summary>
public sealed record ParsedRow(
    int RowNumber,
    DateOnly Date,
    long AmountCents,
    string Description,
    string Merchant,
    string? BankCategory,
    string? BankType,
    TransactionKind Kind)
{
    /// <summary>
    /// Identity used to skip rows already imported into the same account: a SHA-256 of date, amount and
    /// description. Fixed length, and it doesn't keep a second copy of the description.
    /// </summary>
    public string Fingerprint => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{Date:yyyy-MM-dd}|{AmountCents}|{Description.ToUpperInvariant()}")));
}

public sealed record RowError(int RowNumber, string Message);

public sealed record ParsedCsv(BankCsvFormat Format, IReadOnlyList<ParsedRow> Rows, IReadOnlyList<RowError> Errors)
{
    public bool IsCardFormat => Format is BankCsvFormat.ChaseCredit or BankCsvFormat.AppleCard;
}

/// <summary>Detects and parses the three supported bank CSV exports (SPEC section 6.3, milestone 2).</summary>
public static class BankCsvParser
{
    private static readonly string[] DateFormats = ["MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy", "yyyy-MM-dd"];

    public static BankCsvFormat Detect(IEnumerable<string> header)
    {
        var h = header.Select(Norm).ToHashSet();
        if (h.Contains("clearing date") && h.Contains("amount (usd)"))
            return BankCsvFormat.AppleCard;
        if (h.Contains("details") && h.Contains("posting date") && h.Contains("amount"))
            return BankCsvFormat.ChaseDebit;
        if (h.Contains("transaction date") && h.Contains("post date") && h.Contains("amount") && h.Contains("type"))
            return BankCsvFormat.ChaseCredit;
        return BankCsvFormat.Unknown;
    }

    public static ParsedCsv Parse(string text)
    {
        var lines = CsvReader.Parse(text);
        if (lines.Count == 0)
            return new ParsedCsv(BankCsvFormat.Unknown, [], [new RowError(0, "The file is empty.")]);

        var header = lines[0];
        var format = Detect(header);
        if (format == BankCsvFormat.Unknown)
            return new ParsedCsv(format, [], [new RowError(1,
                "Unrecognized file. Supported: Chase checking/savings, Chase credit card and Apple Card CSV exports.")]);

        var col = header.Select((name, i) => (name: Norm(name), i))
            .GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.First().i);
        string? Get(string[] row, string name) =>
            col.TryGetValue(name, out var i) && i < row.Length ? NullIfBlank(row[i]) : null;

        var rows = new List<ParsedRow>();
        var errors = new List<RowError>();
        for (var n = 1; n < lines.Count; n++)
        {
            var rowNumber = n + 1; // 1-based, counting the header line
            try
            {
                rows.Add(format switch
                {
                    BankCsvFormat.ChaseDebit => ParseChaseDebit(lines[n], rowNumber, Get),
                    BankCsvFormat.ChaseCredit => ParseChaseCredit(lines[n], rowNumber, Get),
                    _ => ParseApple(lines[n], rowNumber, Get),
                });
            }
            catch (FormatException ex)
            {
                errors.Add(new RowError(rowNumber, ex.Message));
            }
        }
        return new ParsedCsv(format, rows, errors);
    }

    private delegate string? Getter(string[] row, string name);

    private static ParsedRow ParseChaseDebit(string[] r, int n, Getter get)
    {
        var date = ParseDate(get(r, "posting date"), "Posting Date");
        var bankAmount = ParseCents(get(r, "amount"), "Amount"); // negative = money out
        var description = Required(get(r, "description"), "Description");
        var type = get(r, "type")?.ToUpperInvariant();
        var amount = -bankAmount;

        var kind = type switch
        {
            "ACCT_XFER" => TransactionKind.Transfer,
            "LOAN_PMT" => TransactionKind.CardPayment,
            "FEE_TRANSACTION" => TransactionKind.Fee,
            "DEBIT_CARD" or "REFUND_TRANSACTION" when amount < 0 => TransactionKind.Refund,
            _ when amount < 0 => TransactionKind.Income,
            _ => TransactionKind.Purchase,
        };
        return Row(n, date, amount, description, description, null, type, kind);
    }

    private static ParsedRow ParseChaseCredit(string[] r, int n, Getter get)
    {
        var date = ParseDate(get(r, "transaction date"), "Transaction Date"); // purchase date, per SPEC section 3
        var bankAmount = ParseCents(get(r, "amount"), "Amount"); // purchases negative, payments positive
        var description = Required(get(r, "description"), "Description");
        var type = get(r, "type");
        var amount = -bankAmount;

        var kind = type?.ToUpperInvariant() switch
        {
            "PAYMENT" => TransactionKind.CardPayment,
            "RETURN" => TransactionKind.Refund,
            "FEE" => TransactionKind.Fee,
            "SALE" => TransactionKind.Purchase,
            _ => amount < 0 ? TransactionKind.Refund : TransactionKind.Other,
        };
        return Row(n, date, amount, description, description, get(r, "category"), type, kind);
    }

    private static ParsedRow ParseApple(string[] r, int n, Getter get)
    {
        var date = ParseDate(get(r, "transaction date"), "Transaction Date");
        var amount = ParseCents(get(r, "amount (usd)"), "Amount (USD)"); // purchases positive, payments negative
        var description = Required(get(r, "description"), "Description");
        var type = get(r, "type");
        var category = get(r, "category");

        var kind = type?.ToUpperInvariant() switch
        {
            "PAYMENT" => TransactionKind.CardPayment,
            "CREDIT" => TransactionKind.Refund,
            "INTEREST" => TransactionKind.Fee,
            "PURCHASE" or "INSTALLMENT" => TransactionKind.Purchase,
            _ => amount < 0 ? TransactionKind.Refund : TransactionKind.Other,
        };
        if (kind == TransactionKind.CardPayment)
            category = null; // Apple puts "Payment" in every column of a payment row
        // "Purchased By" (a person's name) is deliberately never read.
        return Row(n, date, amount, description, get(r, "merchant") ?? description, category, type, kind);
    }

    private static ParsedRow Row(int n, DateOnly date, long amount, string description, string merchantSource,
        string? bankCategory, string? bankType, TransactionKind kind) =>
        new(n, date, amount, MerchantText.Sanitize(description), MerchantText.Normalize(MerchantText.Sanitize(merchantSource)),
            bankCategory, bankType, kind);

    public static DateOnly ParseDate(string? value, string column) =>
        DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new FormatException($"{column} '{value}' is not a date (expected MM/DD/YYYY).");

    /// <summary>Parses "-1,234.56", "$12.30", "(12.30)" into exact integer cents. Rejects fractions of a cent.</summary>
    public static long ParseCents(string? value, string column)
    {
        var s = value?.Trim().Replace("$", "").Replace(",", "");
        var negative = false;
        if (s is { Length: > 2 } && s[0] == '(' && s[^1] == ')') { negative = true; s = s[1..^1]; }
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            throw new FormatException($"{column} '{value}' is not an amount.");
        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents))
            throw new FormatException($"{column} '{value}' has fractions of a cent.");
        return (long)(negative ? -cents : cents);
    }

    private static string Required(string? value, string column) =>
        value ?? throw new FormatException($"{column} is blank.");

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Norm(string s) => s.Trim().ToLowerInvariant();
}
