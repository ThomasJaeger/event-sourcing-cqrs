using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Browser;

// The single implementation of IStreamInspector. It reads one aggregate stream through IEventStore and
// shapes it for operator display. The checks run in a fixed precedence, each guarding the next: parse
// first, so a malformed id never reaches the store; then the pm-prefix pre-detection, so a process-manager
// stream returns ProcessManagerUnsupported before ReadStreamAsync would throw UnknownEventTypeException on
// a PM event type the aggregate registry does not hold; then the read. The payload is re-serialized against
// its runtime type with the same JsonSerializerOptions the store writes with (snake_case,
// TenantIdJsonConverter), so the displayed JSON matches the stored shape. Serializing against the static
// IDomainEvent marker would drop the concrete event's properties.
public sealed class StreamInspector : IStreamInspector
{
    private readonly IEventStore _eventStore;
    private readonly JsonSerializerOptions _jsonOptions;

    public StreamInspector(IEventStore eventStore, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _eventStore = eventStore;
        _jsonOptions = jsonOptions;
    }

    public async Task<StreamInspectionResult> InspectStreamAsync(string streamIdInput, CancellationToken ct)
    {
        StreamId streamId;
        try
        {
            streamId = StreamId.Parse(streamIdInput);
        }
        catch (ArgumentException)
        {
            return new StreamInspectionResult(StreamInspectionOutcome.InvalidFormat, []);
        }

        // Pre-detect a process-manager stream by the pm- prefix, the same convention the event store
        // filters aggregate reads with (stream_id NOT LIKE 'pm-%'). Returning it as unsupported here keeps
        // the aggregate read from running and throwing on a PM event type the aggregate registry lacks.
        if (streamId.Value.StartsWith(StreamPrefixes.ProcessManagerPrefix, StringComparison.Ordinal))
        {
            return new StreamInspectionResult(StreamInspectionOutcome.ProcessManagerUnsupported, []);
        }

        var envelopes = await _eventStore.ReadStreamAsync(streamId, fromVersion: 0, ct);
        if (envelopes.Count == 0)
        {
            return new StreamInspectionResult(StreamInspectionOutcome.Empty, []);
        }

        var events = envelopes.Select(ToInspectedEvent).ToList();
        return new StreamInspectionResult(StreamInspectionOutcome.Found, events);
    }

    private InspectedEvent ToInspectedEvent(EventEnvelope envelope)
        => new(
            StreamVersion: envelope.StreamVersion,
            EventType: envelope.EventType,
            EventVersion: envelope.EventVersion,
            OccurredUtc: envelope.OccurredUtc,
            GlobalPosition: envelope.GlobalPosition,
            PayloadJson: JsonSerializer.Serialize(envelope.Payload, envelope.Payload.GetType(), _jsonOptions),
            Metadata: envelope.Metadata);
}
