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
}
