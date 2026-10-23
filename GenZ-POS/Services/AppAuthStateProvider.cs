using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace GenZPOS.Services;

/// <summary>
/// Turns <see cref="CurrentUser"/> into the Blazor authentication state, so pages can use
/// <c>[Authorize(Policy = Permissions.X)]</c> and <c>&lt;AuthorizeView Policy="..."&gt;</c>.
/// Each right is a "perm" claim; each right is a policy that requires that claim.
/// </summary>
public sealed class AppAuthStateProvider : AuthenticationStateProvider, IDisposable
{
    public const string PermissionClaim = "perm";

    private readonly CurrentUser currentUser;

    public AppAuthStateProvider(CurrentUser currentUser)
    {
        this.currentUser = currentUser;
        currentUser.Changed += OnChanged;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(Build());

    private void OnChanged() => NotifyAuthenticationStateChanged(Task.FromResult(Build()));

    private AuthenticationState Build()
    {
        if (currentUser.Session is not { } s)
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, s.UserId.ToString()),
            new(ClaimTypes.Name, s.UserName),
            new(ClaimTypes.Role, s.RoleName),
        };
        claims.AddRange(Permissions.All.Where(s.Has).Select(p => new Claim(PermissionClaim, p)));
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "GenZ-POS")));
    }

    public static void AddPolicies(AuthorizationOptions options)
    {
        foreach (var p in Permissions.All)
            options.AddPolicy(p, b => b.RequireClaim(PermissionClaim, p));
    }

    public void Dispose() => currentUser.Changed -= OnChanged;
}
