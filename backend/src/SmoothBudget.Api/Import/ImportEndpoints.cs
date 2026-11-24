using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Auth;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Import;

/// <param name="BankSynced">True when the account syncs from the bank through Plaid.</param>
public record AccountDto(Guid Id, string Name, string Type, string? Mask, string Source, bool IncludedInBudget, bool BankSynced = false);
public record CreateAccountRequest(string Name, AccountType Type, string? Mask);
public record ImportBatchDto(Guid Id, Guid AccountId, string AccountName, string FileName, string Format, DateTime ImportedUtc,
    int RowCount, int ImportedCount, int DuplicateCount, int ErrorCount);

public static partial class ImportEndpoints
{
    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/accounts").RequireAuthorization();

        accounts.MapGet("", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
            await db.Accounts.Where(a => a.HouseholdId == ctx.User.HouseholdId())
                .OrderBy(a => a.Type).ThenBy(a => a.Name)
                .Select(a => new AccountDto(a.Id, a.Nickname ?? a.Name, a.Type.ToString(), a.Nickname == null ? a.Mask : null, a.Source.ToString(), a.IncludedInBudget, a.PlaidItemId != null))
                .ToListAsync(ct));

        accounts.MapPost("", async (CreateAccountRequest req, HttpContext ctx, AppDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required (up to 100 characters)."] });
            var mask = string.IsNullOrWhiteSpace(req.Mask) ? null : req.Mask.Trim();
            if (mask is not null && !FourDigits().IsMatch(mask))
                // Full account numbers are never stored (CLAUDE.md, Safety).
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mask"] = ["Enter only the last 4 digits."] });
            if (!Enum.IsDefined(req.Type))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["Unknown account type."] });

            var account = new Account
            {
                Id = Guid.NewGuid(), HouseholdId = ctx.User.HouseholdId(), Name = name, Type = req.Type, Mask = mask,
                Source = DataSource.Csv, CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AccountDto(account.Id, account.Name, account.Type.ToString(), account.Mask, account.Source.ToString(), account.IncludedInBudget));
        });

        var import = app.MapGroup("/api/import").RequireAuthorization();

        // Multipart form posts skip ASP.NET's antiforgery token. The auth cookie is SameSite=Lax, so a
        // cross-site form post arrives without it and is rejected as unauthenticated (DECISIONS.md).
        import.MapPost("/csv", async (IFormFile file, [Microsoft.AspNetCore.Mvc.FromForm] Guid accountId, HttpContext ctx,
            ImportService importer, CancellationToken ct) =>
        {
            if (file.Length == 0)
                return Results.Problem("The file is empty.", statusCode: StatusCodes.Status400BadRequest);
            if (file.Length > ImportService.MaxFileBytes)
                return Results.Problem("The file is larger than 5 MB.", statusCode: StatusCodes.Status400BadRequest);

            string text;
            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = await reader.ReadToEndAsync(ct);

            try
            {
                return Results.Ok(await importer.ImportAsync(ctx.User.HouseholdId(), accountId, file.FileName, text, ct));
            }
            catch (ImportException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).DisableAntiforgery();

        import.MapGet("/batches", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.ImportBatches.Where(b => b.HouseholdId == ctx.User.HouseholdId())
                .OrderByDescending(b => b.ImportedUtc).Take(50)
                .Select(b => new { b.Id, b.AccountId, AccountName = b.Account.Nickname ?? b.Account.Name, b.FileName, b.Format, b.ImportedUtc, b.RowCount, b.ImportedCount, b.DuplicateCount, b.ErrorCount })
                .ToListAsync(ct);
            return rows.Select(b => new ImportBatchDto(b.Id, b.AccountId, b.AccountName, b.FileName, ImportService.Describe(b.Format), b.ImportedUtc,
                b.RowCount, b.ImportedCount, b.DuplicateCount, b.ErrorCount));
        });

        import.MapDelete("/batches/{id:guid}", async (Guid id, HttpContext ctx, ImportService importer, CancellationToken ct) =>
            await importer.UndoAsync(ctx.User.HouseholdId(), id, ct) ? Results.NoContent() : Results.NotFound());
    }

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex FourDigits();
}
