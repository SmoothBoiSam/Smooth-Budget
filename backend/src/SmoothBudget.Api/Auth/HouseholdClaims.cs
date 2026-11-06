using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SmoothBudget.Api.Data;

namespace SmoothBudget.Api.Auth;

/// <summary>Puts the household id into the auth cookie so requests don't need a user lookup.</summary>
public class HouseholdClaimsFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(userManager, roleManager, options)
{
    public const string HouseholdClaim = "household_id";

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(HouseholdClaim, user.HouseholdId.ToString()));
        return identity;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid HouseholdId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(HouseholdClaimsFactory.HouseholdClaim), out var id)
            ? id
            : throw new InvalidOperationException("Authenticated user has no household claim.");
}
