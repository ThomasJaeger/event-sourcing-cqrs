using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Migration.Demo.Cdc;
using EventSourcingCqrs.Migration.Demo.LegacyOutbox;
using Npgsql;

namespace EventSourcingCqrs.Migration.Demo.Shadow;

// Chapter 18: shadow mode. This decorates the legacy order service: each surface performs the
// authoritative legacy write first, then emits the corresponding domain event into the event store in
// parallel, so a comparator can check the two agree while the event side is not yet authoritative. It
// mirrors the legacy service's surfaces (place, mark-paid); the legacy service has no cancel, so neither
// does this. Mark-paid reads the legacy row back for the customer and total OrderPlaced carries, which is
// why this holds the legacy connection string beyond the delegated service.
//
// The honesty clause: the two writes land in separate databases and separate transactions. Shadow mode
// does not hide a divergence between them behind a shared transaction; a divergence is exactly what the
// comparator exists to surface.
public sealed class ShadowOrderService
{
    // Lands in EventMetadata.Source so a shadow-emitted event names its writer.
    private const string Source = "migration-shadow";

    private readonly LegacyOrderService _legacyService;
    private readonly EventStreamAppender _appender;
    private readonly string _legacyConnectionString;

    public ShadowOrderService(
        LegacyOrderService legacyService,
        EventStreamAppender appender,
        string legacyConnectionString)
    {
        _legacyService = legacyService;
        _appender = appender;
        _legacyConnectionString = legacyConnectionString;
    }

    public async Task PlaceOrderAsync(
        long orderId, string customerName, decimal total, CancellationToken cancellationToken)
    {
        await _legacyService.PlaceOrderAsync(orderId, customerName, total, cancellationToken);

        var occurredUtc = DateTime.UtcNow;
        var drafted = new OrderDrafted(
            LegacyChangeTranslator.OrderIdFor(orderId),
            LegacyChangeTranslator.CustomerIdFor(customerName),
            occurredUtc,
            LegacyChangeTranslator.LegacyChannel);
        await AppendAsync(orderId, drafted, occurredUtc, cancellationToken);
    }

    public async Task MarkOrderPaidAsync(long orderId, CancellationToken cancellationToken)
    {
        await _legacyService.MarkOrderPaidAsync(orderId, cancellationToken);

        var (customerName, total) = await ReadOrderAsync(orderId, cancellationToken);
        var occurredUtc = DateTime.UtcNow;
        var placed = new OrderPlaced(
            LegacyChangeTranslator.OrderIdFor(orderId),
            LegacyChangeTranslator.CustomerIdFor(customerName),
            new Money(total, Currency.USD),
            occurredUtc);
        await AppendAsync(orderId, placed, occurredUtc, cancellationToken);
    }

    private async Task AppendAsync(
        long orderId, IDomainEvent @event, DateTime occurredUtc, CancellationToken ct)
    {
        var streamId = StreamId.ForAggregate<Order>(
            WellKnownTenants.Default, LegacyChangeTranslator.OrderIdFor(orderId));
        var appended = new[] { new AppendedEvent(@event, Guid.NewGuid(), occurredUtc) };
        await _appender.AppendAsync(streamId, appended, Source, LegacyChangeTranslator.SystemActorId, ct);
    }

    private async Task<(string CustomerName, decimal Total)> ReadOrderAsync(long orderId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_legacyConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT customer_name, total FROM legacy.orders WHERE id = @id";
        command.Parameters.AddWithValue("id", orderId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"Legacy order {orderId} does not exist.");
        }

        return (reader.GetString(0), reader.GetDecimal(1));
    }
}
