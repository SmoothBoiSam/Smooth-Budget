using System.Globalization;
using SmoothBudget.Core.Pay;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmoothBudget.Api.Tests;

/// <summary>
/// Writes an invented pay stub as a PDF in the owner's pay advice layout, including a fake name, address, SSN,
/// employee id and bank account so tests can check that none of them are ever read.
/// </summary>
public static class FakeStubPdf
{
    public const string Name = "Pretend Person", Street = "1 Fake Street", Ssn = "XXX-XX-4321", EmployeeId = "999999", BankAccount = "123456789012";

    /// <param name="taxShift">Moves the tax amounts right of their headings, as another print of the same pay advice does.</param>
    public static byte[] Build(PayStubData s, double taxShift = 0)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var page = b.AddPage(PageSize.Letter);
        const double size = 7;
        double Width(string t)
        {
            var letters = page.MeasureText(t, size, new PdfPoint(0, 0), font);
            return letters.Count == 0 ? 0 : letters.Max(l => l.BoundingBox.Right) - letters.Min(l => l.BoundingBox.Left);
        }
        double T(string t, double x, double y) { page.AddText(t, size, new PdfPoint(x, y), font); return x + Width(t); }
        void R(string t, double right, double y) => T(t, right - Width(t), y);
        string M(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        string D(DateOnly d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

        T("Pay Advice", 331, 770);
        T("Pay Group:", 207, 740); T("FLIGHT", 286, 740); T("Doc No:", 410, 740); T("7654321", 489, 740);
        T(Name, 37, 731); T("Pay Begin Date:", 207, 731); T(D(s.PeriodStart), 286, 731); T("Advice Date:", 410, 731); T(D(s.PayDate), 489, 731);
        T(Street, 37, 722); T("Pay End Date:", 207, 722); T(D(s.PeriodEnd), 286, 722);
        T("SSN: " + Ssn, 37, 700); T("Employee ID:", 219, 700); T(EmployeeId, 287, 700);
        T("Pay Rate:", 219, 650); T(M(s.RateCents), 287, 650); T("Per Credit Hour", 320, 650);

        // Earnings and taxes.
        const double hy = 623;
        T("Description", 40, hy);
        var rateR = T("Rate", 130, hy);
        T("Credit", 156, hy); var hoursR = T("Hours", 176, hy);
        var earnR = T("Earnings", 218, hy); var qtdR = T("Earnings", 269, hy);
        T("Credit", 308, hy); var ytdHoursR = T("Hours", 328, hy); var ytdR = T("Earnings", 366, hy);
        T("Description", 402, hy); var curR = T("Current", 498, hy); var taxYtdR = T("YTD", 558, hy);
        var y = hy - 12;
        for (var i = 0; i < Math.Max(s.Earnings.Count, s.Taxes.Count); i++, y -= 10.5)
        {
            if (i < s.Earnings.Count)
            {
                var e = s.Earnings[i];
                T(e.Name, 39, y);
                if (e.RateCents is { } r) R(M(r), rateR, y);
                if (e.Hours is { } h) R(h.ToString("0.00", CultureInfo.InvariantCulture), hoursR, y);
                R(M(e.CurrentCents), earnR, y);
                R(M(e.CurrentCents * 2), qtdR, y);
                if (e.Hours is { } h2) R((h2 * 9).ToString("0.00", CultureInfo.InvariantCulture), ytdHoursR, y);
                R(M(e.CurrentCents * 9), ytdR, y);
            }
            if (i < s.Taxes.Count)
            {
                T(s.Taxes[i].Name, 400, y);
                R(M(s.Taxes[i].CurrentCents), curR + taxShift, y);
                R(M(s.Taxes[i].CurrentCents * 9), taxYtdR + taxShift, y);
            }
        }
        T("Total:", 40, y); R(M(s.GrossCents), earnR, y); R(M(s.GrossCents * 9), ytdR, y);
        T("Total", 399, y); R(M(s.TotalTaxesCents), curR + taxShift, y); R(M(s.TotalTaxesCents * 9), taxYtdR + taxShift, y);

        // Deductions.
        y -= 20;
        T("EMPLOYER PAID BENEFITS", 81, y); T("BEFORE TAX DEDUCTIONS", 262, y); T("AFTER TAX DEDUCTIONS", 446, y);
        y -= 10;
        T("Description", 38, y); T("Current", 137, y); T("YTD", 199, y);
        T("Description", 222, y); var preR = T("Current", 317, y); var preYtdR = T("YTD", 377, y);
        T("Description", 400, y); var afterR = T("Current", 498, y); var afterYtdR = T("YTD", 560, y);
        for (var i = 0; i < Math.Max(s.PreTax.Count, s.AfterTax.Count); i++)
        {
            y -= 11;
            if (i < s.PreTax.Count) { T(s.PreTax[i].Name, 221, y); R(M(s.PreTax[i].CurrentCents), preR, y); R(M(s.PreTax[i].CurrentCents * 9), preYtdR, y); }
            if (i < s.AfterTax.Count) { T(s.AfterTax[i].Name, 400, y); R(M(s.AfterTax[i].CurrentCents), afterR, y); R(M(s.AfterTax[i].CurrentCents * 9), afterYtdR, y); }
        }
        y -= 12;
        T("Total:", 219, y); R(M(s.PreTax.Sum(p => p.CurrentCents)), preR, y);
        T("Total:", 399, y); R(M(s.AfterTax.Sum(p => p.CurrentCents)), afterR, y);

        // Totals.
        y -= 20;
        T("TOTAL GROSS", 119, y); T("FED TAXABLE GROSS", 197, y); T("TOTAL TAXES", 327, y); T("TOTAL DEDUCTIONS", 407, y); T("NET PAY", 547, y);
        long[] cur = [s.GrossCents, s.FedTaxableCents, s.TotalTaxesCents, s.TotalDeductionsCents, s.NetCents];
        double[] rights = [167, 270, 372, 474, 575];
        y -= 10;
        T("Current:", 38, y);
        for (var i = 0; i < 5; i++) R(M(cur[i]), rights[i], y);
        y -= 8;
        T("YTD:", 38, y);
        for (var i = 0; i < 5; i++) R(M(cur[i] * 9), rights[i], y);

        // Hours and the net pay distribution (a bank account that must never be read).
        y -= 19;
        T("USER HOURS", 69, y); T("VACATION HOURS", 176, y); T("NET PAY DISTRIBUTION", 468, y);
        y -= 14;
        T("Start Balance:", 40, y); R("2.00", 146, y); T("Start Balance:", 153, y); R("3.00", 260, y);
        y -= 14;
        T("+ Earned:", 40, y);
        if (s.UserHoursEarned is { } u) R(u.ToString("0.00", CultureInfo.InvariantCulture), 146, y);
        T("+ Earned:", 153, y);
        if (s.VacationHoursEarned is { } v) R(v.ToString("0.00", CultureInfo.InvariantCulture), 260, y);
        T("Checking", 441, y); T(BankAccount, 480, y); R(M(s.NetCents), 575, y);
        return b.Build();
    }
}
