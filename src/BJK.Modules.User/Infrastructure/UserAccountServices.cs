using BJK.Modules.Shared.PersonalData;
using BJK.Modules.User.Infrastructure.Services;
using BJK.Modules.User.UseCases.Delete;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BJK.Modules.User.Infrastructure;

/// <summary>
/// Runs <see cref="DeleteExpiredAccountsCommand"/> on a timer (hourly by default) in the identity server.
/// Several instances may run it at once; an account is only deleted once.
/// </summary>
internal sealed class AccountRetentionService(
    IServiceScopeFactory scopes,
    IOptions<UserOptions> options,
    TimeProvider clock,
    ILogger<AccountRetentionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RetentionCheckInterval, clock);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var batch = new DeleteExpiredAccountsCommand();
            while (await sender.Send(batch, cancellationToken) == batch.MaxAccounts)
            {
                // A full batch: there may be more due.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The account retention job failed; it will run again at the next interval.");
        }
    }
}

/// <summary>
/// The account section of "download my data": email, status, dates, date of birth, the sign-up liveness result and
/// the age check outcome.
/// </summary>
internal sealed class AccountPersonalDataExporter(IUserRepository users, IIdentityService identity) : IPersonalDataExporter
{
    public string Section => "account";

    public async Task<object?> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await users.GetByIdAsync(userId, cancellationToken);
        var signIn = await identity.GetAccountAsync(userId.ToString("D"), cancellationToken);
        if (account is null && signIn is null)
        {
            return null;
        }

        return new
        {
            Id = userId,
            Email = signIn?.Email,
            EmailConfirmed = signIn?.EmailConfirmed ?? false,
            TwoFactorEnabled = signIn?.TwoFactorEnabled ?? false,
            Roles = signIn?.Roles ?? [],
            Status = account?.Status,
            CreatedAt = account?.CreatedAt,
            DeactivatedAt = account?.DeactivatedAt,
            DeletionDueAt = account?.DeletionDueAt,
            DateOfBirth = account?.DateOfBirth,

            // Result and date only: the video is deleted straight after the check and never stored.
            Liveness = account is null ? null : new
            {
                Status = account.LivenessStatus,
                CheckedAt = account.LivenessCheckedAt,
            },

            // Outcome only: the selfie video and ID photo are deleted straight after the check and never stored.
            AgeCheck = account is null ? null : new
            {
                Status = account.AgeCheckStatus,
                Method = account.AgeCheckMethod,
                Result = account.AgeCheckResult,
                Provider = account.AgeCheckProvider,
                RecordedAt = account.AgeCheckRecordedAt,
            },
        };
    }
}
