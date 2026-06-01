using EventSourcingCqrs.Application;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;

namespace EventSourcingCqrs.Application.Tests.TestKit;

// Shared fixture for the Payment command-handler tests. Constructs the same
// InMemoryEventStore + EventStoreRepository + accessor stack each test wants,
// plus helpers that seed a Payment in a target lifecycle state through the
// repository. Mirrors ShipmentTestFixture, InventoryTestFixture, and
// OrderTestFixture. No Refunded or Voided seed helpers: no command operates
// on those terminal states without throwing.
internal sealed class PaymentTestFixture
{
    public static readonly Guid PaymentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    public static readonly Guid OrderId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    public const string PaymentMethodReference = "PM-REF-9001";
    public const string RefundReason = "Customer requested refund";
    public const string VoidReason = "Cancelled before capture";
    public static readonly Money Amount = new(149.95m, Currency.USD);
    public static readonly DateTime At = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public InMemoryEventStore Store { get; }
    public EventStoreRepository<Payment> Repository { get; }
    public StubCommandContextAccessor Accessor { get; }

    public PaymentTestFixture()
    {
        Store = new InMemoryEventStore();
        Accessor = new StubCommandContextAccessor();
        Repository = new EventStoreRepository<Payment>(Store, Accessor, new StubTenantAccessor());
    }

    public async Task SeedAuthorizedAsync()
    {
        var payment = Payment.Authorize(PaymentId, OrderId, Amount, PaymentMethodReference, At);
        await Repository.SaveAsync(payment, CancellationToken.None);
    }

    public async Task SeedCapturedAsync()
    {
        var payment = Payment.Authorize(PaymentId, OrderId, Amount, PaymentMethodReference, At);
        payment.Capture(At);
        await Repository.SaveAsync(payment, CancellationToken.None);
    }

    public Task<Payment?> LoadAsync() => Repository.LoadAsync(PaymentId, CancellationToken.None);

    public static Guid UnknownId { get; } = Guid.Parse("99999999-9999-9999-9999-999999999999");
}
