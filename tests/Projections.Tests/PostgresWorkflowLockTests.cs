using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public sealed class PostgresWorkflowLockTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Independent_hosts_serialize_one_order_but_allow_nested_commands_and_other_tenants()
    {
        var connection = await fixture.CreateMigratedDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(connection);
        var factory = new NpgsqlReadModelConnectionFactory(source);
        var first = new PostgresWorkflowLock(factory);
        var second = new PostgresWorkflowLock(factory);
        var tenant = WellKnownTenants.Default;
        var order = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = first.RunAsync(tenant, order, async () =>
        {
            await first.RunAsync(tenant, order, () => Task.CompletedTask, default);
            entered.SetResult();
            await release.Task;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var contenderEntered = false;
        var waiting = second.RunAsync(tenant, order, () => { contenderEntered = true; return Task.CompletedTask; }, default);
        await second.RunAsync(TenantId.From(Guid.NewGuid()), order, () => Task.CompletedTask, default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        contenderEntered.Should().BeFalse();
        release.SetResult();
        await Task.WhenAll(held, waiting).WaitAsync(TimeSpan.FromSeconds(5));
        contenderEntered.Should().BeTrue();
    }

    [Fact]
    public async Task Failed_work_releases_the_lock_for_a_different_host()
    {
        var connection = await fixture.CreateMigratedDatabaseAsync();
        await using var source = NpgsqlDataSource.Create(connection);
        var factory = new NpgsqlReadModelConnectionFactory(source);
        var order = Guid.NewGuid();
        Func<Task> work = () => new PostgresWorkflowLock(factory).RunAsync(WellKnownTenants.Default, order,
            () => throw new IOException("worker failed"), default);
        await work.Should().ThrowAsync<IOException>();
        await new PostgresWorkflowLock(factory).RunAsync(WellKnownTenants.Default, order,
            () => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
