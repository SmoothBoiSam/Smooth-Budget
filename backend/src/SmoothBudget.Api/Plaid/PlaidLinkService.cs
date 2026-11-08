using Microsoft.EntityFrameworkCore;
using SmoothBudget.Api.Data;
using SmoothBudget.Api.Import;
using SmoothBudget.Core.Security;

namespace SmoothBudget.Api.Plaid;

public record PlaidAccountProposal(
    string PlaidAccountId, string Name, string? Mask, string Type, string? Subtype,
    // Null when Plaid's account type isn't one the budget tracks (loans, investments).
    string? AccountType,
    // An existing account of the same type and last-4 that this probably is (e.g. one you imported CSVs into).
    Guid? SuggestedAccountId);

/// <param name="DuplicateOfItemId">This bank is already connected through that connection: add accounts there instead.</param>
public record LinkResultDto(Guid ItemId, string? InstitutionName, IReadOnlyList<PlaidAccountProposal> Accounts, IReadOnlyList<AccountDto> ExistingAccounts,
    Guid? DuplicateOfItemId = null);

/// <param name="Action">"new" (create an account), "existing" (link to <paramref name="AccountId"/>) or "skip".</param>
public record AccountMappingRequest(string PlaidAccountId, string Action, Guid? AccountId);

public record PlaidItemDto(Guid Id, string? InstitutionName, string Status, string? StatusMessage, DateTime? LastSyncedUtc, IReadOnlyList<string> Accounts);

public class PlaidSetupException(string message) : Exception(message);

/// <summary>Linking a bank: exchange the Link token, choose accounts, unlink.</summary>
public class PlaidLinkService(AppDbContext db, PlaidClient plaid, SecretBox secrets, PlaidSyncService sync, TimeProvider clock, ILogger<PlaidLinkService> log)
{
    public async Task<LinkResultDto> ExchangeAsync(Guid householdId, string publicToken, CancellationToken ct)
    {
        var exchanged = await plaid.ExchangePublicTokenAsync(publicToken, ct);
        var accounts = await plaid.GetAccountsAsync(exchanged.AccessToken, ct);
        string? institutionName = null;
        if (accounts.Item.InstitutionId is { } instId)
        {
            try { institutionName = await plaid.GetInstitutionNameAsync(instId, ct); }
            catch (PlaidException ex) { log.LogInformation("Institution name lookup failed: {Code}", ex.ErrorCode); }
        }

        var item = await db.PlaidItems.SingleOrDefaultAsync(i => i.PlaidItemId == exchanged.ItemId, ct);
        if (item is not null && item.HouseholdId != householdId)
            throw new PlaidSetupException("This bank connection belongs to another household.");
        if (item is null)
        {
            item = new PlaidItem
            {
                Id = Guid.NewGuid(), HouseholdId = householdId, PlaidItemId = exchanged.ItemId,
                AccessTokenEncrypted = secrets.Encrypt(exchanged.AccessToken), Status = PlaidItemStatus.PendingSetup,
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.PlaidItems.Add(item);
        }
        else
        {
            item.AccessTokenEncrypted = secrets.Encrypt(exchanged.AccessToken);
        }
        item.InstitutionId = accounts.Item.InstitutionId;
        item.InstitutionName = institutionName;
        await db.SaveChangesAsync(ct);

        // Signing in to a bank that's already connected makes a second connection. Adding an account (a new savings)
        // belongs on the first one instead (DECISIONS.md 157), so say so before anything is set up twice.
        var twin = item.InstitutionId is null ? null : await db.PlaidItems.AsNoTracking()
            .Where(i => i.HouseholdId == householdId && i.Id != item.Id && i.InstitutionId == item.InstitutionId && i.Status != PlaidItemStatus.PendingSetup)
            .OrderBy(i => i.CreatedUtc).FirstOrDefaultAsync(ct);
        return await ProposalsAsync(householdId, item, accounts, ct) with { DuplicateOfItemId = twin?.Id };
    }

    /// <summary>The accounts at an existing connection, after adding accounts to it through Plaid (DECISIONS.md 157).</summary>
    public async Task<LinkResultDto> AccountsAsync(Guid householdId, Guid itemId, CancellationToken ct)
    {
        var item = await db.PlaidItems.SingleOrDefaultAsync(i => i.Id == itemId && i.HouseholdId == householdId, ct)
            ?? throw new PlaidSetupException("Bank connection not found.");
        return await ProposalsAsync(householdId, item, await plaid.GetAccountsAsync(secrets.Decrypt(item.AccessTokenEncrypted), ct), ct);
    }

    private async Task<LinkResultDto> ProposalsAsync(Guid householdId, PlaidItem item, AccountsGetResponse accounts, CancellationToken ct)
    {
        var existing = await db.Accounts.Where(a => a.HouseholdId == householdId).OrderBy(a => a.Name).ToListAsync(ct);
        var proposals = accounts.Accounts.Select(pa =>
        {
            var type = AccountTypeFor(pa.Type, pa.Subtype);
            var mask = Last4(pa.Mask);
            var already = existing.FirstOrDefault(a => a.PlaidAccountId == pa.AccountId);
            var suggestion = already ?? (type is null || mask is null ? null
                : existing.FirstOrDefault(a => a.Type == type && a.Mask == mask && a.PlaidAccountId == null));
            return new PlaidAccountProposal(pa.AccountId, pa.OfficialName ?? pa.Name, mask, pa.Type, pa.Subtype, type?.ToString(), suggestion?.Id);
        }).ToList();

        return new LinkResultDto(item.Id, item.InstitutionName, proposals,
            existing.Select(a => new AccountDto(a.Id, a.Name, a.Type.ToString(), a.Mask, a.Source.ToString(), a.IncludedInBudget, a.PlaidItemId != null)).ToList());
    }

    public async Task<SyncResultDto> ConfirmAccountsAsync(Guid householdId, Guid itemId, IReadOnlyList<AccountMappingRequest> mappings, CancellationToken ct)
    {
        var item = await db.PlaidItems.SingleOrDefaultAsync(i => i.Id == itemId && i.HouseholdId == householdId, ct)
            ?? throw new PlaidSetupException("Bank connection not found.");
        var plaidAccounts = (await plaid.GetAccountsAsync(secrets.Decrypt(item.AccessTokenEncrypted), ct)).Accounts.ToDictionary(a => a.AccountId);
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var m in mappings)
        {
            if (!plaidAccounts.TryGetValue(m.PlaidAccountId, out var pa))
                throw new PlaidSetupException("Unknown account in this connection.");
            if (m.Action == "skip")
                continue;
            var type = AccountTypeFor(pa.Type, pa.Subtype)
                ?? throw new PlaidSetupException($"\"{pa.Name}\" is a {pa.Type} account, which the budget doesn't track.");

            Account account;
            if (m.Action == "existing")
            {
                account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == m.AccountId && a.HouseholdId == householdId, ct)
                    ?? throw new PlaidSetupException("Account not found.");
                if (account.Type != type)
                    throw new PlaidSetupException($"\"{account.Name}\" is a {account.Type} account but \"{pa.Name}\" is {type}.");
                if (account.PlaidAccountId is not null && account.PlaidAccountId != pa.AccountId)
                    throw new PlaidSetupException($"\"{account.Name}\" already syncs through your other {item.InstitutionName ?? "bank"} connection. "
                        + "To add an account there, tap Add accounts on that connection, then disconnect this one.");
            }
            else if (m.Action == "new")
            {
                account = await db.Accounts.SingleOrDefaultAsync(a => a.PlaidAccountId == pa.AccountId && a.HouseholdId == householdId, ct)
                    ?? db.Accounts.Add(new Account
                    {
                        Id = Guid.NewGuid(), HouseholdId = householdId, Name = Truncate(pa.Name, 100), Type = type,
                        Source = DataSource.Plaid, CreatedUtc = now,
                    }).Entity;
            }
            else
            {
                throw new PlaidSetupException($"Unknown action '{m.Action}'.");
            }

            account.PlaidAccountId = pa.AccountId;
            account.PlaidItemId = item.Id;
            account.Mask ??= Last4(pa.Mask);
            if (pa.Balances.Current is { } current)
                account.CurrentBalanceCents = PlaidSyncService.BalanceCents(type, current);
            if (pa.Balances.Limit is { } limit)
                account.CreditLimitCents = (long)Math.Round(limit * 100m, 0, MidpointRounding.AwayFromZero);
        }

        item.Status = PlaidItemStatus.Active;
        await db.SaveChangesAsync(ct);
        return await sync.SyncAsync(item.Id, ct);
    }

    /// <summary>Disconnects from Plaid. The accounts and their history stay; they just stop syncing.</summary>
    public async Task<bool> RemoveAsync(Guid householdId, Guid itemId, CancellationToken ct)
    {
        var item = await db.PlaidItems.SingleOrDefaultAsync(i => i.Id == itemId && i.HouseholdId == householdId, ct);
        if (item is null)
            return false;
        try
        {
            await plaid.RemoveItemAsync(secrets.Decrypt(item.AccessTokenEncrypted), ct);
        }
        catch (PlaidException ex)
        {
            log.LogWarning("Plaid item/remove failed for {ItemId}: {Code}; removing locally anyway", item.Id, ex.ErrorCode);
        }
        await db.Accounts.Where(a => a.PlaidItemId == item.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PlaidItemId, (Guid?)null).SetProperty(a => a.PlaidAccountId, (string?)null), ct);
        db.PlaidItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<PlaidItemDto>> ListAsync(Guid householdId, CancellationToken ct)
    {
        var items = await db.PlaidItems.Where(i => i.HouseholdId == householdId).OrderBy(i => i.CreatedUtc).ToListAsync(ct);
        var ids = items.Select(i => i.Id).ToList();
        var accounts = await db.Accounts.Where(a => a.PlaidItemId != null && ids.Contains(a.PlaidItemId.Value))
            .Select(a => new { a.PlaidItemId, a.Name, a.Mask }).ToListAsync(ct);
        return items.Select(i => new PlaidItemDto(i.Id, i.InstitutionName, i.Status.ToString(), i.StatusMessage, i.LastSyncedUtc,
            accounts.Where(a => a.PlaidItemId == i.Id).Select(a => a.Mask is null ? a.Name : $"{a.Name} ••{a.Mask}").ToList())).ToList();
    }

    public static AccountType? AccountTypeFor(string type, string? subtype) => (type.ToLowerInvariant(), subtype?.ToLowerInvariant()) switch
    {
        ("credit", _) => AccountType.CreditCard,
        ("depository", "savings" or "money market" or "cd") => AccountType.Savings,
        ("depository", _) => AccountType.Checking,
        _ => null,
    };

    /// <summary>Only the last four digits are ever stored.</summary>
    public static string? Last4(string? mask)
    {
        var digits = new string((mask ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : digits.Length <= 4 ? digits : digits[^4..];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
