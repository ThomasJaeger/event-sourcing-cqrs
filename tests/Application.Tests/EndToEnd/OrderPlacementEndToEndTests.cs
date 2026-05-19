using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.EndToEnd;

// Drives the full order-placement workflow through the bus and back out
// through the query. Each command dispatch pushes events through
// PostgresEventStore -> outbox -> LISTEN/NOTIFY -> OrderListProjection ->
// read_models.order_list. ListOrders is the final query that proves the
// command and query buses are wired to the same machinery the host runs.
public class OrderPlacementEndToEndTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(3);

    private readonly PostgresFixture _fixture;

    public OrderPlacementEndToEndTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlaceOrder_workflow_dispatches_through_the_bus_and_appears_in_ListOrders()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        using var host = WorkersHostFactory.Build(connStr, connStr);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.StartAsync(cts.Token);
        try
        {
            var commandBus = host.Services.GetRequiredService<ICommandBus>();
            var queryBus = host.Services.GetRequiredService<IQueryBus>();
            var orderId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var address = new Address("1 Main St", "Smalltown", "12345", "US");

            await commandBus.SendAsync(new DraftOrder(orderId, customerId), cts.Token);
            await commandBus.SendAsync(
                new AddOrderLine(orderId, lineId, "SKU-1", 2, new Money(10m, "USD")),
                cts.Token);
            await commandBus.SendAsync(new SetOrderShippingAddress(orderId, address), cts.Token);
            await commandBus.SendAsync(new PlaceOrder(orderId), cts.Token);

            var row = await PollForRowAsync(queryBus, orderId, PollBudget, cts.Token);

            row.Should().NotBeNull(
                "OrderPlaced should reach order_list end-to-end within {0} via LISTEN/NOTIFY",
                PollBudget);
            row!.OrderId.Should().Be(orderId);
            row.Status.Should().Be(OrderStatus.Placed);
            row.Total.Should().Be(new Money(20m, "USD"));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<OrderListRow?> PollForRowAsync(
        IQueryBus queryBus, Guid orderId, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var page = await queryBus.AskAsync(new ListOrders(Offset: 0, Limit: 100), ct);
            var match = page.FirstOrDefault(r => r.OrderId == orderId);
            if (match is not null) return match;
            await Task.Delay(PollInterval, ct);
        }
        var finalPage = await queryBus.AskAsync(new ListOrders(Offset: 0, Limit: 100), ct);
        return finalPage.FirstOrDefault(r => r.OrderId == orderId);
    }
}
