using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using KurrentDB.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// The KurrentDB read-side dispatch mechanism: a catch-up subscription over the $all feed that plays
// aggregate events into the in-process dispatcher, the native counterpart to the relational outbox
// processor (CLAUDE.md: "Native catch-up subscriptions used for projections instead of polling").
// KurrentDB pushes committed events to a live subscription, so there is no poll loop and no outbox
// table; the subscription's own $all position is the dispatch checkpoint, kept under its own name in
// the same checkpoint store the projections use.
public sealed class KurrentSubscriptionService : BackgroundService
{
    // The dispatch checkpoint's name, distinct from every projection checkpoint. Projection names are
    // the eight per-projection constants in ProjectionNames; this is the subscription's own read
    // position over $all, so it shares the checkpoint table with them under a name none of them uses.
    public const string CheckpointName = "kurrent-subscription-dispatch";

    private readonly KurrentDBClient _client;
    private readonly EventUpcasterPipeline _pipeline;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IMessageDispatcher _dispatcher;
    private readonly KurrentSubscriptionOptions _options;
    private readonly ILogger<KurrentSubscriptionService> _logger;

    public KurrentSubscriptionService(
        KurrentDBClient client,
        JsonSerializerOptions jsonOptions,
        ICheckpointStore checkpointStore,
        IMessageDispatcher dispatcher,
        IOptions<KurrentSubscriptionOptions> options,
        ILogger<KurrentSubscriptionService> logger,
        EventUpcasterPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pipeline);
        _client = client;
        _jsonOptions = jsonOptions;
        _checkpointStore = checkpointStore;
        _dispatcher = dispatcher;
        _options = options.Value;
        _logger = logger;
        _pipeline = pipeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reconnect-on-fault, mirroring the outbox processors' back-off-and-retry loop: the
        // subscription runs until it faults or the host stops; a fault logs and backs off, then
        // resubscribes from the stored checkpoint. At-least-once by construction: an event dispatched
        // but not yet checkpointed before a fault is redelivered on reconnect, and per-handler
        // idempotency (projection GREATEST checkpoints, keyed process-manager dispatch) absorbs it.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSubscriptionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "KurrentDB subscription faulted; reconnecting from the stored checkpoint");
                try
                {
                    await Task.Delay(_options.ReconnectBackoff, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task RunSubscriptionAsync(CancellationToken ct)
    {
        var checkpoint = await _checkpointStore.GetPositionAsync(CheckpointName, ct);

        // An empty checkpoint reads from the start of $all; a stored one resumes after it. FromAll.After
        // is exclusive, so the event sitting on the checkpoint never redelivers (SQ1). The stored value
        // is a commit position, reconstructed into the (commit, prepare) pair the adapter writes it as.
        var start = checkpoint <= 0
            ? FromAll.Start
            : FromAll.After(new Position((ulong)checkpoint, (ulong)checkpoint));

        // The same server-side stream filter ReadAllAsync uses, so system and PM streams never reach a
        // handler. checkpointInterval drives how often a filtered stretch reports its position.
        var filter = new SubscriptionFilterOptions(
            StreamFilter.RegularExpression(KurrentEventStore.AggregateFeedStreamFilter),
            checkpointInterval: _options.CheckpointInterval);

        // The checkpoint advances on a dispatched event and on a server checkpoint message, and on
        // nothing else. Through a short filtered tail (system or PM events after the last dispatched
        // event, below KurrentDB's catch-up checkpoint batch) it lags at the last dispatched position by
        // design: resume correctness holds because the server-side filter replays the tail as skips on
        // reconnect, and a lag metric reading this checkpoint shows honest staleness through a filtered
        // quiet period rather than a false zero. This tail-lag characteristic is recorded in ADR 0047.
        await foreach (var message in _client
            .SubscribeToAll(start, filterOptions: filter, cancellationToken: ct)
            .Messages
            .WithCancellation(ct))
        {
            switch (message)
            {
                case StreamMessage.Event matched:
                    var outboxMessage = KurrentEventHydration.ToOutboxMessage(
                        matched.ResolvedEvent, _pipeline, _jsonOptions);
                    await _dispatcher.DispatchAsync(outboxMessage, ct);
                    await _checkpointStore.AdvanceAsync(
                        CheckpointName, outboxMessage.GlobalPosition, ct);
                    break;

                // A filtered stretch (system or PM streams) surfaces no event to dispatch, but the
                // checkpoint-reached signal carries the $all position past it (SQ2), so the dispatch
                // checkpoint still advances and a reconnect does not re-scan the stretch.
                case StreamMessage.AllStreamCheckpointReached reached:
                    await _checkpointStore.AdvanceAsync(
                        CheckpointName, checked((long)reached.Position.CommitPosition), ct);
                    break;

                default:
                    break;
            }
        }
    }
}
