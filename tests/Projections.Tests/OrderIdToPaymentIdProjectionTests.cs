using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Projections.OrderIdToPaymentId;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class OrderIdToPaymentIdProjectionTests
{
    private static readonly DateTime AuthorizedAt = new(2026, 5, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Money TenUsd = new(10m, Currency.USD);

    [Fact]
    public async Task PaymentAuthorized_handler_records_the_order_to_payment_mapping()
    {
        var store = new InMemoryOrderIdToPaymentIdStore();
        var projection = new OrderIdToPaymentIdProjection(store);
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await projection.HandleAsync(
            Context(new PaymentAuthorized(paymentId, orderId, TenUsd, "card", AuthorizedAt), position: 1),
            CancellationToken.None);

        (await store.GetPaymentIdAsync(orderId, CancellationToken.None)).Should().Be(paymentId);
    }

    [Fact]
    public async Task Handler_advances_the_checkpoint_under_the_projection_name()
    {
        var store = new InMemoryOrderIdToPaymentIdStore();
        var projection = new OrderIdToPaymentIdProjection(store);
        projection.Name.Should().Be("order-id-to-payment-id");

        await projection.HandleAsync(
            Context(new PaymentAuthorized(Guid.NewGuid(), Guid.NewGuid(), TenUsd, "card", AuthorizedAt), position: 7),
            CancellationToken.None);

        store.Checkpoints[projection.Name].Should().Be(7);
    }

    [Fact]
    public async Task Handler_skips_when_position_is_at_or_below_checkpoint()
    {
        var store = new InMemoryOrderIdToPaymentIdStore();
        var projection = new OrderIdToPaymentIdProjection(store);
        // First record advances the checkpoint to 10.
        await projection.HandleAsync(
            Context(new PaymentAuthorized(Guid.NewGuid(), Guid.NewGuid(), TenUsd, "card", AuthorizedAt), position: 10),
            CancellationToken.None);
        var recordsAfterFirst = store.RecordCount;
        var laterOrder = Guid.NewGuid();

        // A second PaymentAuthorized at position 10 (or below) is an at-least-once
        // redelivery from the projection's perspective: the handler returns early
        // without touching the unit of work.
        await projection.HandleAsync(
            Context(new PaymentAuthorized(Guid.NewGuid(), laterOrder, TenUsd, "card", AuthorizedAt), position: 10),
            CancellationToken.None);

        store.RecordCount.Should().Be(recordsAfterFirst);
        (await store.GetPaymentIdAsync(laterOrder, CancellationToken.None)).Should().BeNull();
        store.Checkpoints[projection.Name].Should().Be(10);
    }

    [Fact]
    public async Task A_second_payment_for_the_same_order_keeps_the_first_mapping()
    {
        var store = new InMemoryOrderIdToPaymentIdStore();
        var projection = new OrderIdToPaymentIdProjection(store);
        var orderId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await projection.HandleAsync(
            Context(new PaymentAuthorized(first, orderId, TenUsd, "card", AuthorizedAt), position: 1),
            CancellationToken.None);

        // An anomalous second PaymentAuthorized for the same order at a later
        // position. ON CONFLICT DO NOTHING keeps the first mapping.
        await projection.HandleAsync(
            Context(new PaymentAuthorized(second, orderId, TenUsd, "card", AuthorizedAt), position: 2),
            CancellationToken.None);

        (await store.GetPaymentIdAsync(orderId, CancellationToken.None)).Should().Be(first);
    }

    private static EventContext<TEvent> Context<TEvent>(TEvent @event, long position)
        where TEvent : IDomainEvent
        => new(
            @event,
            new EventMetadata(
                EventId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.Empty,
                Source: "test",
                SchemaVersion: 1,
                OccurredUtc: AuthorizedAt,
                Tenant: WellKnownTenants.Default),
            position);
}
