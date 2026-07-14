using EventSourcingCqrs.Infrastructure.EventStore.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SqlServer;

// Composition facts for the SQL Server processor registrations. These resolve services; they open
// no connections, so they need no container.
//
// The invariant: an option a host sets has to reach the object that consumes it. The retry
// policies are where that can fail in silence. Both processor options types carry BaseSeconds and
// CapSeconds whose defaults equal the policy's own constructor defaults, and the DI container
// honors constructor default parameter values. So a registration that never reads the options
// still yields a working policy on the default curve: every other test stays green, the processor
// backs off correctly out of the box, and an operator who raises the backoff gets nothing. Only a
// non-default value can tell the two registrations apart.
//
// The PostgreSQL twins of these two facts live in Postgres/ServiceCollectionExtensionsTests and
// are the control. They run the same probe against a registration that does read its options, so
// if they pass and these fail, the defect is the registration and not the probe.
public class ServiceCollectionExtensionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);

    // Non-default on both axes, and off the default curve at both observation points: the default
    // first retry is 1 second and the default cap is 300.
    private const int ConfiguredBaseSeconds = 7;
    private const int ConfiguredCapSeconds = 11;

    [Fact]
    public void AddSqlServerOutboxProcessor_builds_the_retry_policy_from_the_configured_options()
    {
        var services = new ServiceCollection();
        services.AddSqlServerOutboxProcessor();
        services.AddSingleton<IOptions<SqlServerOutboxProcessorOptions>>(
            Options.Create(new SqlServerOutboxProcessorOptions
            {
                BaseSeconds = ConfiguredBaseSeconds,
                CapSeconds = ConfiguredCapSeconds,
            }));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<SqlServerOutboxRetryPolicy>();

        // The backoff curve is the policy's whole public surface, so the configured values are
        // observed through it rather than through its fields. With jitter pinned to 1.0 the first
        // retry lands exactly BaseSeconds out, and a high attempt count saturates at CapSeconds.
        policy.ComputeNextAttempt(attemptCount: 1, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredBaseSeconds));
        policy.ComputeNextAttempt(attemptCount: 15, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredCapSeconds));
    }

    [Fact]
    public void AddSqlServerDelayQueueProcessor_builds_the_retry_policy_from_the_configured_options()
    {
        var services = new ServiceCollection();
        services.AddSqlServerDelayQueueProcessor();
        services.AddSingleton<IOptions<SqlServerDelayQueueProcessorOptions>>(
            Options.Create(new SqlServerDelayQueueProcessorOptions
            {
                BaseSeconds = ConfiguredBaseSeconds,
                CapSeconds = ConfiguredCapSeconds,
            }));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<SqlServerDelayQueueRetryPolicy>();

        policy.ComputeNextAttempt(attemptCount: 1, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredBaseSeconds));
        policy.ComputeNextAttempt(attemptCount: 15, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredCapSeconds));
    }
}
