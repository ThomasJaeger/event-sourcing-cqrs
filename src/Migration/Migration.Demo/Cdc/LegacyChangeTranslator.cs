using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Migration.Demo.Cdc;

// Chapter 18: owns the legacy-to-domain vocabulary mapping. A legacy change row carries a CRUD row
// image; this turns it into the domain events the event store speaks. The load-bearing decision is
// that a legacy status transition to "paid" is what the domain calls placing the order, so a paid
// update maps to OrderPlaced; other transitions carry no domain meaning here and are skipped.
//
// A legacy order's bigint primary key and text customer name have no event-native identity, so this
// type also owns the deterministic mapping to the Guids the domain events carry; the reader keys its
// streams on the same mapping.
public sealed class LegacyChangeTranslator
{
    // The channel a CDC-sourced draft carries: these orders entered through the legacy system, not a
    // live host, so the channel names that origin rather than a web or store front.
    public const string LegacyChannel = "legacy";

    // The legacy status that means the order has been placed. A CRUD update to this status is the
    // transition the domain records as OrderPlaced.
    public const string PlacedStatus = "paid";

    private const string LegacyDeletionReason = "Order removed in the legacy system.";

    // The system actor that stands in for the legacy system as the originator of CDC-emitted events.
    // A stable hand-generated constant, so an audit query for CDC-originated events filters on one
    // Guid that does not drift across runs.
    public static readonly Guid SystemActorId = Guid.Parse("9b2c8f5e-3a41-4d7c-8e6f-1a2b3c4d5e6f");

    // A legacy order's event-store identity: the bigint primary key placed in the low eight bytes of
    // a Guid, so distinct legacy ids map to distinct, stable stream identities.
    public static Guid OrderIdFor(long legacyOrderId)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], legacyOrderId);
        return new Guid(bytes);
    }

    // A legacy customer's event-store identity, derived from the CRUD row's customer name. The legacy
    // schema has no customer id, so the name is the only stable key to hash.
    public static Guid CustomerIdFor(string customerName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(customerName));
        return new Guid(hash.AsSpan(0, 16));
    }

    // Maps one legacy change to the domain events it means. Insert drafts the order; a paid update
    // places it; a delete cancels it. A non-paid update is a CRUD edit with no domain event here and
    // returns nothing.
    public IReadOnlyList<IDomainEvent> Translate(char operation, string rowPayloadJson, DateTime occurredUtc)
    {
        using var document = JsonDocument.Parse(rowPayloadJson);
        var row = document.RootElement;
        var orderId = OrderIdFor(row.GetProperty("id").GetInt64());
        var customerId = CustomerIdFor(row.GetProperty("customer_name").GetString() ?? string.Empty);

        return operation switch
        {
            'I' => [new OrderDrafted(orderId, customerId, occurredUtc, LegacyChannel)],
            'U' => TranslateUpdate(row, orderId, customerId, occurredUtc),
            'D' => [new OrderCancelled(orderId, LegacyDeletionReason, SystemActorId, occurredUtc)],
            _ => [],
        };
    }

    private static IReadOnlyList<IDomainEvent> TranslateUpdate(
        JsonElement row, Guid orderId, Guid customerId, DateTime occurredUtc)
    {
        if (row.GetProperty("status").GetString() != PlacedStatus)
        {
            return [];
        }

        // The legacy schema carries no currency, so the demo fixes the event money to USD.
        var total = new Money(row.GetProperty("total").GetDecimal(), Currency.USD);
        return [new OrderPlaced(orderId, customerId, total, occurredUtc)];
    }
}
