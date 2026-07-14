using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Infrastructure.Persistence;

namespace SyncCoordinator.Web.Authentication;

public sealed class AdminAccountService(
    CoordinatorDbContext dbContext,
    IPasswordHasher<AdminAccountEntity> passwordHasher,
    TimeProvider timeProvider)
{
    public const string AdministratorUserName = "admin";
    public const string SessionVersionClaim = "sync_coordinator_session_version";
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;

    public Task<bool> IsInitializedAsync(CancellationToken cancellationToken) =>
        dbContext.AdminAccounts.AsNoTracking().AnyAsync(cancellationToken);

    public async Task<AdminSignInResult> VerifyAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        var account = await dbContext.AdminAccounts
            .SingleOrDefaultAsync(x => x.UserName == AdministratorUserName, cancellationToken);
        if (account is null ||
            !string.Equals(userName.Trim(), AdministratorUserName, StringComparison.OrdinalIgnoreCase))
        {
            return AdminSignInResult.Failed;
        }

        var result = passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return AdminSignInResult.Failed;
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = passwordHasher.HashPassword(account, password);
            account.UpdatedAtUtc = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new AdminSignInResult(true, account.UserName, account.SessionVersion);
    }

    public async Task<AdminPasswordResult> InitializeAsync(
        string password,
        string confirmation,
        CancellationToken cancellationToken)
    {
        var validation = ValidateNewPassword(password, confirmation);
        if (!validation.Succeeded)
        {
            return validation;
        }
        if (await dbContext.AdminAccounts.AnyAsync(cancellationToken))
        {
            return AdminPasswordResult.Failure("already_initialized");
        }

        var now = timeProvider.GetUtcNow();
        var account = new AdminAccountEntity
        {
            Id = Guid.NewGuid(),
            UserName = AdministratorUserName,
            PasswordHash = string.Empty,
            SessionVersion = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        account.PasswordHash = passwordHasher.HashPassword(account, password);
        dbContext.AdminAccounts.Add(account);
        AddAudit(account, "Initialized", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return AdminPasswordResult.Success(account.SessionVersion);
    }

    public async Task<AdminPasswordResult> ResetAsync(
        string password,
        string confirmation,
        CancellationToken cancellationToken)
    {
        var validation = ValidateNewPassword(password, confirmation);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var account = await dbContext.AdminAccounts
            .SingleOrDefaultAsync(x => x.UserName == AdministratorUserName, cancellationToken);
        if (account is null)
        {
            return await InitializeAsync(password, confirmation, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        account.PasswordHash = passwordHasher.HashPassword(account, password);
        account.SessionVersion++;
        account.UpdatedAtUtc = now;
        AddAudit(account, "PasswordReset", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return AdminPasswordResult.Success(account.SessionVersion);
    }

    public async Task<AdminPasswordResult> ChangeAsync(
        string currentPassword,
        string newPassword,
        string confirmation,
        CancellationToken cancellationToken)
    {
        var validation = ValidateNewPassword(newPassword, confirmation);
        if (!validation.Succeeded)
        {
            return validation;
        }

        var account = await dbContext.AdminAccounts
            .SingleOrDefaultAsync(x => x.UserName == AdministratorUserName, cancellationToken);
        if (account is null ||
            passwordHasher.VerifyHashedPassword(account, account.PasswordHash, currentPassword) ==
            PasswordVerificationResult.Failed)
        {
            return AdminPasswordResult.Failure("current_password_invalid");
        }

        var now = timeProvider.GetUtcNow();
        account.PasswordHash = passwordHasher.HashPassword(account, newPassword);
        account.SessionVersion++;
        account.UpdatedAtUtc = now;
        AddAudit(account, "PasswordChanged", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return AdminPasswordResult.Success(account.SessionVersion);
    }

    public async Task<bool> IsSessionCurrentAsync(
        string userName,
        int sessionVersion,
        CancellationToken cancellationToken) =>
        await dbContext.AdminAccounts.AsNoTracking().AnyAsync(
            x => x.UserName == userName && x.SessionVersion == sessionVersion,
            cancellationToken);

    public static AdminPasswordResult ValidateNewPassword(string password, string confirmation)
    {
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            return AdminPasswordResult.Failure("password_mismatch");
        }
        if (password.Length < MinimumPasswordLength)
        {
            return AdminPasswordResult.Failure("password_too_short");
        }
        if (password.Length > MaximumPasswordLength)
        {
            return AdminPasswordResult.Failure("password_too_long");
        }
        return AdminPasswordResult.Success(0);
    }

    private void AddAudit(AdminAccountEntity account, string action, DateTimeOffset now) =>
        dbContext.ConfigurationAudits.Add(new ConfigurationAuditEntity
        {
            Id = Guid.NewGuid(),
            ConfigurationType = "AdminAccount",
            ConfigurationId = account.Id.ToString("N"),
            ConfigurationName = account.UserName,
            Action = action,
            BeforeJson = null,
            AfterJson = JsonSerializer.Serialize(new { account.UserName, account.SessionVersion }),
            ChangedBy = action is "Initialized" or "PasswordReset" ? "localhost-recovery" : "administrator",
            ChangedAtUtc = now
        });
}

public sealed record AdminSignInResult(bool Succeeded, string UserName, int SessionVersion)
{
    public static AdminSignInResult Failed { get; } = new(false, string.Empty, 0);
}

public sealed record AdminPasswordResult(bool Succeeded, string? ErrorCode, int SessionVersion)
{
    public static AdminPasswordResult Success(int sessionVersion) => new(true, null, sessionVersion);
    public static AdminPasswordResult Failure(string errorCode) => new(false, errorCode, 0);
}
