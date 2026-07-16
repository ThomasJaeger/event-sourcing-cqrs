using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class ServiceCollectionExtensionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);

    // Non-default on both axes, and off the default curve at both observation points: the default
    // first retry is 1 second and the default cap is 300.
    private const int ConfiguredBaseSeconds = 7;
    private const int ConfiguredCapSeconds = 11;

    [Fact]
    public void AddPostgresEventStore_resolves_registered_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<INpgsqlConnectionFactory>()
            .Should().BeOfType<NpgsqlConnectionFactory>();
        provider.GetRequiredService<EventTypeRegistry>().Should().NotBeNull();
        provider.GetRequiredService<JsonSerializerOptions>().Should().NotBeNull();
        provider.GetRequiredService<IEventStore>().Should().BeOfType<PostgresEventStore>();
        provider.GetService<IMessageDispatcher>().Should().BeNull(
            "AddPostgresEventStore does not register the outbox dispatcher; "
            + "AddPostgresOutboxProcessor does (split rationale in commit history).");
        provider.GetServices<IHostedService>().OfType<OutboxProcessor>()
            .Should().BeEmpty(
                "AddPostgresEventStore does not register the outbox processor; "
                + "AddPostgresOutboxProcessor does.");
    }

    [Fact]
    public void AddEventStoreHeadPosition_resolves_the_head_position_port()
    {
        var services = new ServiceCollection();
        services.AddEventStoreHeadPosition("Host=localhost;Database=stub");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEventStoreHeadPosition>()
            .Should().BeOfType<PostgresEventStoreHeadReader>();
        provider.GetRequiredService<INpgsqlConnectionFactory>()
            .Should().BeOfType<NpgsqlConnectionFactory>();
    }

    [Fact]
    public void AddEventStoreHeadPosition_does_not_register_the_full_event_store()
    {
        var services = new ServiceCollection();
        services.AddEventStoreHeadPosition("Host=localhost;Database=stub");

        using var provider = services.BuildServiceProvider();

        provider.GetService<IEventStore>().Should().BeNull(
            "the focused head-position registration composes only the head read; "
            + "a host needing the full event store uses AddPostgresEventStore.");
        provider.GetService<JsonSerializerOptions>().Should().BeNull(
            "the head read needs no serializer; AddPostgresEventStore registers that.");
    }

    [Fact]
    public void AddPostgresOutboxProcessor_registers_the_dispatcher_and_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddPostgresOutboxProcessor();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<OutboxRetryPolicy>().Should().NotBeNull();
        provider.GetRequiredService<IMessageDispatcher>()
            .Should().BeOfType<InProcessMessageDispatcher>();
        provider.GetServices<IHostedService>().OfType<OutboxProcessor>()
            .Should().ContainSingle();
    }

    // The two facts below are the control for the SQL Server twins in
    // SqlServer/ServiceCollectionExtensionsTests. The wiring they pin is correct on disk and has
    // been since it was written, but nothing pinned it, and the same registration on the SQL
    // Server side reads no options at all. The defect class is engine-general: both processor
    // options types carry BaseSeconds and CapSeconds whose defaults equal the policy's own
    // constructor defaults, and the container honors constructor default parameter values, so a
    // registration that forgets the options still hands back a policy on the default curve.
    // Declared as characterization: green on write, and asserting behavior that already held.
    [Fact]
    public void AddPostgresOutboxProcessor_builds_the_retry_policy_from_the_configured_options()
    {
        var services = new ServiceCollection();
        services.AddPostgresOutboxProcessor();
        services.AddSingleton<IOptions<OutboxProcessorOptions>>(
            Options.Create(new OutboxProcessorOptions
            {
                BaseSeconds = ConfiguredBaseSeconds,
                CapSeconds = ConfiguredCapSeconds,
            }));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<OutboxRetryPolicy>();

        // Observed through the curve, the policy's whole public surface. Jitter pinned to 1.0: the
        // first retry lands exactly BaseSeconds out, a high attempt count saturates at CapSeconds.
        policy.ComputeNextAttempt(attemptCount: 1, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredBaseSeconds));
        policy.ComputeNextAttempt(attemptCount: 15, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredCapSeconds));
    }

    [Fact]
    public void AddPostgresDelayQueueProcessor_builds_the_retry_policy_from_the_configured_options()
    {
        var services = new ServiceCollection();
        services.AddPostgresDelayQueueProcessor();
        services.AddSingleton<IOptions<DelayQueueProcessorOptions>>(
            Options.Create(new DelayQueueProcessorOptions
            {
                BaseSeconds = ConfiguredBaseSeconds,
                CapSeconds = ConfiguredCapSeconds,
            }));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<DelayQueueRetryPolicy>();

        policy.ComputeNextAttempt(attemptCount: 1, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredBaseSeconds));
        policy.ComputeNextAttempt(attemptCount: 15, Now, jitter: 1.0)
            .Should().Be(Now.AddSeconds(ConfiguredCapSeconds));
    }

    [Fact]
    public void AddPostgresEventStore_populates_EventTypeRegistry_from_registered_providers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // IEventTypeProvider registrations may land before or after
        // AddPostgresEventStore in any order: GetServices<IEventTypeProvider>()
        // inside the factory enumerates at first-resolution time, not at
        // registration time. This test calls AddPostgresEventStore *before*
        // registering the providers, which would break a naive eager wiring.
        services.AddPostgresEventStore(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddSingleton<IEventTypeProvider, FirstProvider>();
        services.AddSingleton<IEventTypeProvider, SecondProvider>();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<EventTypeRegistry>();

        registry.NameFor(typeof(EventOne)).Should().Be(nameof(EventOne));
        registry.NameFor(typeof(EventTwo)).Should().Be(nameof(EventTwo));
        registry.NameFor(typeof(EventThree)).Should().Be(nameof(EventThree));
    }

    [Fact]
    public void AddPostgresEventStore_populates_ProcessManagerEventTypeRegistry_from_registered_providers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresEventStore(opts =>
            opts.ConnectionString = "Host=localhost;Database=stub");
        services.AddSingleton<IProcessManagerEventTypeProvider, FirstPmProvider>();
        services.AddSingleton<IProcessManagerEventTypeProvider, SecondPmProvider>();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ProcessManagerEventTypeRegistry>();

        registry.NameFor(typeof(PmEventOne)).Should().Be(nameof(PmEventOne));
        registry.NameFor(typeof(PmEventTwo)).Should().Be(nameof(PmEventTwo));
        registry.NameFor(typeof(PmEventThree)).Should().Be(nameof(PmEventThree));
    }

    private sealed record EventOne : IDomainEvent;
    private sealed record EventTwo : IDomainEvent;
    private sealed record EventThree : IDomainEvent;

    private sealed class FirstProvider : IEventTypeProvider
    {
        public IEnumerable<Type> GetEventTypes() => [typeof(EventOne), typeof(EventTwo)];
    }

    private sealed class SecondProvider : IEventTypeProvider
    {
        public IEnumerable<Type> GetEventTypes() => [typeof(EventThree)];
    }

    private sealed record PmEventOne : IProcessManagerEvent;
    private sealed record PmEventTwo : IProcessManagerEvent;
    private sealed record PmEventThree : IProcessManagerEvent;

    private sealed class FirstPmProvider : IProcessManagerEventTypeProvider
    {
        public IEnumerable<Type> GetEventTypes() => [typeof(PmEventOne), typeof(PmEventTwo)];
    }

    private sealed class SecondPmProvider : IProcessManagerEventTypeProvider
    {
        public IEnumerable<Type> GetEventTypes() => [typeof(PmEventThree)];
    }
}
