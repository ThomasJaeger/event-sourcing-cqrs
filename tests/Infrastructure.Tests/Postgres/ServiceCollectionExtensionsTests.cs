using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class ServiceCollectionExtensionsTests
{
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
        provider.GetRequiredService<OutboxRetryPolicy>().Should().NotBeNull();
        provider.GetRequiredService<IMessageDispatcher>()
            .Should().BeOfType<InProcessMessageDispatcher>();
        provider.GetServices<IHostedService>().OfType<OutboxProcessor>()
            .Should().ContainSingle();
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
}
