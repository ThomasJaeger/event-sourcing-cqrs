using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// RED for slice 4's connection-string overload of AddPostgresDelayQueueProcessor. The no-argument
// overload composes the processor beside AddPostgresEventStore on a relational host; the KurrentDB
// host has no such call, so it composes the processor on its read-model PostgreSQL database through
// this overload, the same posture AddPostgresIdempotencyStore and AddPostgresDelayQueue already take.
// The overload's body is empty until the GREEN slice, so both facts fail against it: resolution finds
// no hosted processor, and the drain fact cannot resolve one to drive.
public class DelayQueueProcessorConnectionStringOverloadTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    private static readonly DateTimeOffset FireAt = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DelayQueueProcessorConnectionStringOverloadTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void The_connection_string_overload_registers_the_hosted_delay_queue_processor()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ICausedCommandBus, OverloadRecordingBus>();
        services.AddPostgresDelayQueueProcessor("Host=localhost;Database=stub");

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().OfType<DelayQueueProcessor>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task The_connection_string_overload_drains_a_due_scheduled_command()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var bus = new OverloadRecordingBus();
        services.AddSingleton<ICausedCommandBus>(bus);
        services.AddSingleton<ICommandTypeProvider, OverloadTimeoutCommandTypeProvider>();
        // The delay queue registers IDelayQueue so the fact can schedule; the overload under test
        // registers the processor that drains it, both against the same read-model database.
        services.AddPostgresDelayQueue(connStr);
        services.AddPostgresDelayQueueProcessor(connStr);
        using var provider = services.BuildServiceProvider();

        var queue = provider.GetRequiredService<IDelayQueue>();
        var stream = StreamId.Parse($"pm-order-fulfillment:{Guid.NewGuid():N}");
        await queue.ScheduleAsync(
            new OverloadTimeout("fire"), FireAt, stream, "await-payment",
            CausingEvent(), SystemActors.OrderFulfillment, "key-1", CancellationToken.None);

        var processor = provider.GetServices<IHostedService>().OfType<DelayQueueProcessor>().Single();
        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        bus.Received.Should().ContainSingle().Which.Should().BeOfType<OverloadTimeout>();
    }

    private static EventMetadata CausingEvent()
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "Sales",
            OccurredUtc: new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
            Tenant: WellKnownTenants.Default);
}

public sealed record OverloadTimeout(string Note) : ICommand;

internal sealed class OverloadTimeoutCommandTypeProvider : ICommandTypeProvider
{
    public IEnumerable<Type> GetCommandTypes() => [typeof(OverloadTimeout)];
}

internal sealed class OverloadRecordingBus : ICausedCommandBus
{
    public List<ICommand> Received { get; } = [];

    public Task SendAsync(
        ICommand command,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string? idempotencyKey,
        CancellationToken ct)
    {
        Received.Add(command);
        return Task.CompletedTask;
    }
}
