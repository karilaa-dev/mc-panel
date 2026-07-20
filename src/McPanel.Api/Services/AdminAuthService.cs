using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using McPanel.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed partial class AdminAuthService(
    IDbContextFactory<StateDbContext> stateFactory,
    IPasswordHasher<AdminEntity> hasher,
    PanelPaths paths,
    IOptions<PanelOptions> options,
    SessionAudience audience,
    IHubContext<PanelHub> hub,
    ILogger<AdminAuthService> logger)
{
    public const string SessionStampClaim = "mcpanel:session_stamp";

    public async Task<AuthStatusDto> StatusAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var admin = await db.Admins.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return new(admin is null, user.Identity?.IsAuthenticated == true && admin is not null,
            user.Identity?.IsAuthenticated == true && admin is not null ? new AdminDto(admin.Username) : null);
    }

    public async Task<AdminDto> SetupAsync(HttpContext context, SetupRequest request, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Admins.AnyAsync(cancellationToken)) throw new PanelException(409, "SETUP_DISABLED", "Initial setup has already been completed.");
        if (request is null || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.Username) || request.Password is null)
            throw PanelProblems.Validation("Setup token, username, and password are required.");
        ValidateCredentials(request.Username, request.Password);
        var expected = ReadSetupToken();
        if (string.IsNullOrWhiteSpace(expected) || !FixedEquals(expected.Trim(), request.Token.Trim()))
            throw new PanelException(401, "SETUP_TOKEN_INVALID", "The one-time setup token is invalid.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Admins.AnyAsync(cancellationToken)) throw new PanelException(409, "SETUP_DISABLED", "Initial setup has already been completed.");
        var admin = new AdminEntity { Username = request.Username.Trim(), PasswordHash = "pending" };
        admin.PasswordHash = hasher.HashPassword(admin, request.Password);
        db.Admins.Add(admin); await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        var revokedGroup = await audience.SetCurrentAsync(admin.SessionStamp, CancellationToken.None);
        await NotifyRevokedAsync(revokedGroup);
        TryRemoveSetupTokenFile();
        await SignInAsync(context, admin);
        return new AdminDto(admin.Username);
    }

    public async Task<AdminDto> LoginAsync(HttpContext context, LoginRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || request.Password is null)
            throw new PanelException(401, "AUTH_INVALID", "The username or password is incorrect.");
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var admin = await db.Admins.SingleOrDefaultAsync(cancellationToken);
        if (admin is null || !FixedEquals(admin.Username, request.Username.Trim()) || hasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            throw new PanelException(401, "AUTH_INVALID", "The username or password is incorrect.");
        if (string.IsNullOrWhiteSpace(admin.SessionStamp))
        {
            RotateSessionStamp(admin);
            var revokedGroup = await audience.RotateAfterPersistAsync(admin.SessionStamp,
                () => db.SaveChangesAsync(CancellationToken.None), CancellationToken.None);
            await NotifyRevokedAsync(revokedGroup);
        }
        await SignInAsync(context, admin);
        return new AdminDto(admin.Username);
    }

    public async Task LogoutAsync(HttpContext context)
    {
        var id = UserId(context.User);
        if (id != 0)
        {
            await using var db = await stateFactory.CreateDbContextAsync(context.RequestAborted);
            var admin = await db.Admins.FindAsync([id], context.RequestAborted);
            if (admin is not null)
            {
                RotateSessionStamp(admin);
                var revokedGroup = await audience.RotateAfterPersistAsync(admin.SessionStamp,
                    () => db.SaveChangesAsync(CancellationToken.None), CancellationToken.None);
                await NotifyRevokedAsync(revokedGroup);
            }
        }
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task ChangePasswordAsync(HttpContext context, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.CurrentPassword is null || request.NewPassword is null) throw PanelProblems.Validation("Current and new passwords are required.");
        ValidatePassword(request.NewPassword);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var id = UserId(context.User);
        var admin = await db.Admins.FindAsync([id], cancellationToken) ?? throw new PanelException(401, "AUTH_INVALID", "Authentication is no longer valid.");
        if (hasher.VerifyHashedPassword(admin, admin.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new PanelException(401, "AUTH_INVALID", "The current password is incorrect.");
        admin.PasswordHash = hasher.HashPassword(admin, request.NewPassword);
        RotateSessionStamp(admin);
        var revokedGroup = await audience.RotateAfterPersistAsync(admin.SessionStamp,
            () => db.SaveChangesAsync(CancellationToken.None), CancellationToken.None);
        await NotifyRevokedAsync(revokedGroup);
        await SignInAsync(context, admin);
    }

    public async Task<bool> ValidateSessionAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        var id = UserId(user);
        var stamp = user?.FindFirstValue(SessionStampClaim);
        if (id == 0 || string.IsNullOrWhiteSpace(stamp)) return false;
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        return await db.Admins.AsNoTracking().AnyAsync(x => x.Id == id && x.SessionStamp == stamp, cancellationToken);
    }

    private static async Task SignInAsync(HttpContext context, AdminEntity admin)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim(ClaimTypes.Name, admin.Username),
            new Claim(ClaimTypes.Role, "Administrator"),
            new Claim(SessionStampClaim, admin.SessionStamp)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
    }

    private string? ReadSetupToken()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.SetupToken)) return options.Value.SetupToken;
        try { return File.Exists(paths.SetupTokenFile) ? File.ReadAllText(paths.SetupTokenFile) : null; }
        catch { return null; }
    }

    private void TryRemoveSetupTokenFile()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.SetupToken)) return;
        try { if (File.Exists(paths.SetupTokenFile)) File.Delete(paths.SetupTokenFile); } catch { }
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left)); var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static int UserId(ClaimsPrincipal? user) =>
        int.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : 0;

    private static void RotateSessionStamp(AdminEntity admin) => admin.SessionStamp = Guid.NewGuid().ToString("N");

    private async Task NotifyRevokedAsync(string? group)
    {
        if (group is null) return;
        try { await hub.Clients.Group(group).SendAsync("SessionRevoked", CancellationToken.None); }
        catch (Exception exception) { logger.LogDebug(exception, "Could not notify revoked live sessions"); }
    }

    private static void ValidateCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || !UsernameRegex().IsMatch(username.Trim())) throw PanelProblems.Validation("Username must be 3-32 letters, numbers, '.', '-' or '_'.");
        ValidatePassword(password);
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 12 or > 256) throw PanelProblems.Validation("Password must be between 12 and 256 characters.");
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UsernameRegex();
}
