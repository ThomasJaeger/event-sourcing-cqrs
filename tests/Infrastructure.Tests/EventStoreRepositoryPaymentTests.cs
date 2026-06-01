using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using EventSourcingCqrs.Infrastructure.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

// Round-trips the Payment aggregate through the generic EventStoreRepository
// against the in-memory event store. Parallel to EventStoreRepositoryShipmentTests
// (commit 10) and EventStoreRepositoryInventoryTests (commit 7). Validates that
// the generic repository handles Payment without per-aggregate code, and that
// typed Money payloads on three of Payment's four events (Authorized, Captured,
// Refunded) serialize and rehydrate through the same path Order and Shipment use.
public class EventStoreRepositoryPaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OrderId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
    private const string PaymentMethodReference = "PM-REF-9001";
    private const string RefundReason = "Customer requested refund";
    private static readonly Money Amount = new(149.95m, Currency.USD);
    private static readonly DateTime At = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trip_returns_equivalent_aggregate()
    {
        var store = new InMemoryEventStore();
        var repo = new EventStoreRepository<Payment>(store, new StubAccessor(), new StubTenantAccessor());

        var payment = Payment.Authorize(PaymentId, OrderId, Amount, PaymentMethodReference, At);
        payment.Capture(At);
        payment.Refund(RefundReason, At);

        await repo.SaveAsync(payment, CancellationToken.None);
        var loaded = await repo.LoadAsync(PaymentId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(PaymentId);
        loaded.OrderId.Should().Be(OrderId);
        loaded.Status.Should().Be(PaymentStatus.Refunded);
        loaded.AuthorizedAmount.Should().Be(Amount);
        loaded.CapturedAmount.Should().Be(Amount);
        loaded.Version.Should().Be(3);
    }
}
