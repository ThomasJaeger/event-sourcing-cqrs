using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// RED for slice 2's companion decomposition (the loop ruling). AddPostgresIdempotencyStore and
// AddPostgresDelayQueue let a KurrentDB host compose the two companion ports on its read-model
// PostgreSQL database without calling AddPostgresEventStore. The skeletons register nothing, so
// every fact below fails at resolution; the GREEN slice fills them.
//
// Sited with the other Postgres DI tests. The behavior facts migrate a fresh database with the full
// set, which is the read-model-database posture: command_idempotency and delayed_commands, with
// their tenant columns, land there through the unconditional read-model migration run.
public class CompanionDecompositionExtensionsTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    private static readonly DateTimeOffset FireAt = new(2026, 5, 21, 12, 30, 0, TimeSpan.Zero);

    public CompanionDecompositionExtensionsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AddPostgresIdempotencyStore_resolves_the_idempotency_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresIdempotencyStore("Host=localhost;Database=stub");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IIdempotencyStore>()
            .Should().BeOfType<PostgresIdempotencyStore>();
    }

    [Fact]
    public void AddPostgresDelayQueue_resolves_the_delay_queue()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ICommandTypeProvider, CompanionCommandTypeProvider>();
        services.AddPostgresDelayQueue("Host=localhost;Database=stub");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDelayQueue>()
            .Should().BeOfType<PostgresDelayQueue>();
    }

    [Fact]
    public async Task AddPostgresIdempotencyStore_round_trips_a_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPostgresIdempotencyStore(connStr);
        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IIdempotencyStore>();

        var tenant = WellKnownTenants.Default;
        (await store.ExistsAsync(tenant, "key-1", CancellationToken.None)).Should().BeFalse();
        (await store.TryRecordAsync(tenant, "key-1", "SomeCommand", CancellationToken.None))
            .Should().BeTrue();
        (await store.ExistsAsync(tenant, "key-1", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task AddPostgresDelayQueue_round_trips_a_scheduled_command()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ICommandTypeProvider, CompanionCommandTypeProvider>();
        services.AddPostgresDelayQueue(connStr);
        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IDelayQueue>();

        var stream = NewPmStream();
        await queue.ScheduleAsync(
            new CompanionScheduledCommand("timeout"), FireAt, stream, "await-payment",
            CausingEvent(), SystemActors.OrderFulfillment, "key-1", CancellationToken.None);

        var cancelled = await queue.CancelAsync(
            stream, "await-payment", "payment arrived", CancellationToken.None);

        cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task AddPostgresDelayQueue_captures_the_serializer_shape_over_a_foreign_bare_options()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // The Kurrent-host situation: a foreign bare JsonSerializerOptions is already in the
        // container, with default (PascalCase) naming and no TenantIdJsonConverter. If the extension
        // resolved this instead of capturing the Postgres shape, the stored command_payload would
        // carry PascalCase keys, so the raw-column assertion below distinguishes the two shapes. The
        // encoder cannot: default STJ already escapes non-ASCII, so a round-trip alone would not tell
        // a captured shape from a resolved one; the naming policy is what does.
        services.AddSingleton(new JsonSerializerOptions());
        services.AddSingleton<ICommandTypeProvider, CompanionCommandTypeProvider>();
        services.AddPostgresDelayQueue(connStr);
        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IDelayQueue>();

        var stream = NewPmStream();
        await queue.ScheduleAsync(
            new CompanionScheduledCommand("héllo"), FireAt, stream, "await-payment",
            CausingEvent(), SystemActors.OrderFulfillment, "key-1", CancellationToken.None);

        var payloadJson = await ReadCommandPayloadAsync(connStr, stream.Value, "await-payment");
        payloadJson.Should().Contain("scheduled_note").And.NotContain("ScheduledNote");
    }

    private static StreamId NewPmStream()
        => StreamId.Parse($"pm-order-fulfillment:{Guid.NewGuid():N}");

    private static EventMetadata CausingEvent()
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.NewGuid(),
            Source: "Sales",
            SchemaVersion: 1,
            OccurredUtc: new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc),
            Tenant: WellKnownTenants.Default);

    private static async Task<string> ReadCommandPayloadAsync(
        string connStr, string streamValue, string step)
    {
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT command_payload::text FROM event_store.delayed_commands " +
            "WHERE scheduled_by_stream_id = @stream AND scheduled_by_step = @step";
        cmd.Parameters.AddWithValue("stream", streamValue);
        cmd.Parameters.AddWithValue("step", step);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}

public sealed record CompanionScheduledCommand(string ScheduledNote) : ICommand;

internal sealed class CompanionCommandTypeProvider : ICommandTypeProvider
{
    public IEnumerable<Type> GetCommandTypes() => [typeof(CompanionScheduledCommand)];
}
