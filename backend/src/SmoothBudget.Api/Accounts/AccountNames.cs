using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Accounts;

/// <summary>
/// What an account is called everywhere in the app (DECISIONS.md 310): its nickname when you gave it one, else its name
/// with its last four digits ("Chase Checking ••1234").
/// </summary>
public static class AccountNames
{
    public const int MaxNickname = 40;

    public static string Label(string name, string? mask, string? nickname) =>
        !string.IsNullOrWhiteSpace(nickname) ? nickname : mask is null ? name : $"{name} ••{mask}";

    public static string Label(Account a) => Label(a.Name, a.Mask, a.Nickname);

    /// <summary>Its name without the digits: the nickname, or the name from the bank.</summary>
    public static string Short(Account a) => string.IsNullOrWhiteSpace(a.Nickname) ? a.Name : a.Nickname;

    /// <summary>What it was called before any nickname: how labels saved earlier (a sweep's destination) name it.</summary>
    public static string Imported(Account a) => Label(a.Name, a.Mask, null);

    /// <summary>
    /// A label saved earlier (a sweep's destination), as the account is called now; for an account that's closed, the one
    /// that took over from it (DECISIONS.md 318). Pass every account, closed ones included.
    /// </summary>
    public static string Current(IEnumerable<Account> accounts, string saved)
    {
        var list = accounts as IReadOnlyCollection<Account> ?? accounts.ToList();
        return list.FirstOrDefault(a => Imported(a) == saved || Label(a) == saved || a.Name == saved) is { } a ? Label(Successor(list, a)) : saved;
    }

    /// <summary>
    /// The account that's open now in place of this one: itself when open, else what replaced it (followed along), else your
    /// oldest open account of the same kind. A closed account with nothing open like it is still itself.
    /// </summary>
    public static Account Successor(IEnumerable<Account> accounts, Account a)
    {
        var list = accounts as IReadOnlyCollection<Account> ?? accounts.ToList();
        var byId = list.ToDictionary(x => x.Id);
        for (var hops = 0; a.ClosedOn is not null && a.ReplacedById is { } next && byId.TryGetValue(next, out var b) && hops < 10; hops++)
            a = b;
        if (a.ClosedOn is null) return a;
        var type = a.Type;
        return list.Where(x => x.ClosedOn is null && x.Type == type).OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).FirstOrDefault() ?? a;
    }
}
