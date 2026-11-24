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

    /// <summary>A label saved earlier (a sweep's destination), as the account is called now.</summary>
    public static string Current(IEnumerable<Account> accounts, string saved) =>
        accounts.FirstOrDefault(a => Imported(a) == saved || Label(a) == saved || a.Name == saved) is { } a ? Label(a) : saved;
}
