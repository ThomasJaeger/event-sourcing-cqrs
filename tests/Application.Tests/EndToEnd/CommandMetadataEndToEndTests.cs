using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.EndToEnd;

// Dispatches one command through the bus that the Workers host composed and
// reads the stored event back through IEventStore. The assertion is that the
// metadata on the persisted envelope carries the real values the bus pushed
// onto the accessor, not the Guid.Empty / "Domain" placeholders the
// pre-commit-5 code wrote.
public class CommandMetadataEndToEndTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public CommandMetadataEndToEndTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DraftOrder_persists_with_real_metadata_from_the_command_context()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(EventStoreProvider.Postgres, connStr, connStr);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.StartAsync(cts.Token);
        try
        {
            var bus = (CommandBus)host.Services.GetRequiredService<ICommandBus>();
            var eventStore = host.Services.GetRequiredService<IEventStore>();
            var orderId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var correlationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

            await bus.SendAsync(new DraftOrder(orderId, customerId), correlationId, cts.Token);

            var envelopes = await eventStore.ReadStreamAsync(
                StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId), fromVersion: 0, cts.Token);
            envelopes.Should().ContainSingle();
            var metadata = envelopes[0].Metadata;
            metadata.CorrelationId.Should().Be(correlationId);
            metadata.CausationId.Should().NotBe(Guid.Empty);
            metadata.Source.Should().Be("Workers");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
