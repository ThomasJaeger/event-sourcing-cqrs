using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.IntegrationTests.Commands.Billing;

internal static class PaymentSeed
{
    private static readonly DateTime SeedUtc =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Money Amount = new Money(50.00m, Currency.USD);

    public static Task AuthorizedAsync(IEventStore eventStore, Guid paymentId, CancellationToken ct = default)
    {
        var events = new IDomainEvent[]
        {
            new PaymentAuthorized(paymentId, Guid.NewGuid(), Amount, "pm-ref-test", SeedUtc),
        };
        return EventStoreSeed.AppendAsync(eventStore, StreamId.ForAggregate<Payment>(WellKnownTenants.Default, paymentId), events, ct);
    }

    public static Task CapturedAsync(IEventStore eventStore, Guid paymentId, CancellationToken ct = default)
    {
        var events = new IDomainEvent[]
        {
            new PaymentAuthorized(paymentId, Guid.NewGuid(), Amount, "pm-ref-test", SeedUtc),
            new PaymentCaptured(paymentId, Amount, SeedUtc),
        };
        return EventStoreSeed.AppendAsync(eventStore, StreamId.ForAggregate<Payment>(WellKnownTenants.Default, paymentId), events, ct);
    }
}
