using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Hosts.Workers;

// Grants the configured administrator the Admin role on host startup, so the system has an assigner
// before any role assignment can be made. Idempotent: if the administrator already holds Admin it is
// a no-op, and the optimistic-concurrency check on the UserRoles stream makes a concurrent seed from
// a second host safe (one append wins; the loser sees the role already present on its load).
//
// Runs in IHostedLifecycleService.StartingAsync, the same lifecycle phase ProjectionStartupCatchUpService
// uses, before steady-state delivery begins. The append goes through a scoped
// IEventStoreRepository<UserRoles>, so the service opens a scope (the repository is scoped; this
// service is a singleton). The appended event flows to the current-roles read model through the
// normal outbox dispatch.
public sealed class BootstrapAdministratorService : IHostedLifecycleService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BootstrapAdministratorOptions _options;
    private readonly ILogger<BootstrapAdministratorService> _logger;

    public BootstrapAdministratorService(
        IServiceScopeFactory scopeFactory,
        IOptions<BootstrapAdministratorOptions> options,
        ILogger<BootstrapAdministratorService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartingAsync(CancellationToken ct)
    {
        if (_options.AdministratorUserId == Guid.Empty)
        {
            _logger.LogInformation(
                "No bootstrap administrator configured; skipping the administrator seed.");
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEventStoreRepository<UserRoles>>();

        var existing = await repository.LoadAsync(_options.AdministratorUserId, ct);
        if (existing is null)
        {
            var userRoles = UserRoles.Create(_options.AdministratorUserId, Role.Admin);
            await repository.SaveAsync(userRoles, ct);
            _logger.LogInformation(
                "Bootstrapped administrator {AdministratorUserId} with the Admin role.",
                _options.AdministratorUserId);
            return;
        }

        if (!existing.Roles.Contains(Role.Admin))
        {
            existing.Assign(Role.Admin);
            await repository.SaveAsync(existing, ct);
            _logger.LogInformation(
                "Granted the Admin role to existing user {AdministratorUserId}.",
                _options.AdministratorUserId);
            return;
        }

        _logger.LogInformation(
            "Administrator {AdministratorUserId} already holds the Admin role; seed is a no-op.",
            _options.AdministratorUserId);
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
