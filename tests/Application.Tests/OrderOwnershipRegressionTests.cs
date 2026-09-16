using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public sealed class OrderOwnershipRegressionTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("place")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("address")]
    public async Task Customer_cannot_mutate_another_customers_order(string operation)
    {
        var f = new OrderTestFixture();
        await f.SeedReadyToPlaceAsync();
        f.Accessor.Current = new StubCommandContext { ActorId = Guid.NewGuid(), Roles = [Role.Customer],
            AuthorizationMode = DispatchAuthorizationMode.AuthenticatedUser };
        Func<Task> act = operation switch
        {
            "cancel" => () => new CancelOrderHandler(f.Repository, f.Accessor, new NoShipmentCancellationGuard())
                .HandleAsync(new CancelOrder(OrderTestFixture.OrderId, "cancel", Guid.NewGuid()), default),
            "place" => () => new PlaceOrderHandler(f.Repository, f.Accessor).HandleAsync(new PlaceOrder(OrderTestFixture.OrderId), default),
            "add" => () => new AddOrderLineHandler(f.Repository, f.Accessor).HandleAsync(new AddOrderLine(OrderTestFixture.OrderId, Guid.NewGuid(), "sku", 1, OrderTestFixture.TenUsd), default),
            "remove" => () => new RemoveOrderLineHandler(f.Repository, f.Accessor).HandleAsync(new RemoveOrderLine(OrderTestFixture.OrderId, OrderTestFixture.LineId1), default),
            _ => () => new SetOrderShippingAddressHandler(f.Repository, f.Accessor).HandleAsync(new SetOrderShippingAddress(OrderTestFixture.OrderId, OrderTestFixture.Shipping), default)
        };
        await act.Should().ThrowAsync<UnauthorizedCommandException>();
        (await f.LoadAsync())!.Status.Should().Be(OrderStatus.Draft);
    }

    [Fact]
    public async Task Customer_cannot_draft_for_another_customer()
    {
        var f = new OrderTestFixture();
        f.Accessor.Current = new StubCommandContext { ActorId = Guid.NewGuid(), Roles = [Role.Customer],
            AuthorizationMode = DispatchAuthorizationMode.AuthenticatedUser };
        Func<Task> act = () => new DraftOrderHandler(f.Repository, f.Accessor)
            .HandleAsync(new DraftOrder(OrderTestFixture.OrderId, OrderTestFixture.CustomerId), default);
        await act.Should().ThrowAsync<UnauthorizedCommandException>();
        (await f.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_records_authenticated_actor_instead_of_forged_payload()
    {
        var f = new OrderTestFixture();
        await f.SeedDraftedAsync();
        f.Accessor.Current = new StubCommandContext { ActorId = OrderTestFixture.CustomerId, Roles = [Role.Customer],
            AuthorizationMode = DispatchAuthorizationMode.AuthenticatedUser };
        await new CancelOrderHandler(f.Repository, f.Accessor, new NoShipmentCancellationGuard())
            .HandleAsync(new CancelOrder(OrderTestFixture.OrderId, "cancel", Guid.NewGuid()), default);
        var events = await f.Store.ReadStreamAsync(StreamId.ForAggregate<Order>(WellKnownTenants.Default, OrderTestFixture.OrderId));
        var cancelled = events.Last();
        ((OrderCancelled)cancelled.Payload).IssuedByUserId.Should().Be(OrderTestFixture.CustomerId);
        cancelled.Metadata.ActorId.Should().Be(OrderTestFixture.CustomerId);
    }
}
