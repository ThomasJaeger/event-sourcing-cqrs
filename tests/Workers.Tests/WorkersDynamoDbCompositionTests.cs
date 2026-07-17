using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

// The Workers DynamoDb composition, mirroring WorkersKurrentCompositionTests: the event store on
// DynamoDB, the idempotency store and delay queue on the read-model PostgreSQL database (ADR 0046's
// non-relational posture, which DynamoDB inherits), the delay-queue processor draining PM timeouts
// there, and the stream dispatch service in the outbox processor's place.
//
// This host composes DI only, so placeholder settings suffice: no adapter opens a connection until
// the host starts, which this fact never does. DynamoDB is addressed by a service URL rather than a
// connection string, and Build's address parameter carries it, because that parameter is the engine's
// address whatever shape the engine takes rather than a DSN slot.
//
// No table name is passed, so the arm leaves the adapter's default standing. That is the state this
// fact wants: Program.cs reads the table-name key and hands it down, and the default is the only
// value both hosts reach without an operator naming it twice.
public class WorkersDynamoDbCompositionTests
{
    private const string DynamoDbServiceUrl = "http://localhost:4566";
    private const string ReadModelConnectionString = "Host=localhost;Database=stub";

    [Fact]
    public void Building_the_DynamoDb_arm_composes_the_write_side_and_the_stream_dispatch_service()
    {
        using var host = WorkersHostFactory.Build(
            EventStoreProvider.DynamoDb, DynamoDbServiceUrl, ReadModelConnectionString);
        var services = host.Services;

        // The event store is DynamoDB; the companion ports land on the read-model PostgreSQL
        // database, because DynamoDB holds only events and IEventStore gains no methods (ADR 0046).
        services.GetRequiredService<IEventStore>().Should().BeOfType<DynamoDbEventStore>();
        services.GetRequiredService<IIdempotencyStore>().Should().BeOfType<PostgresIdempotencyStore>();
        services.GetRequiredService<IDelayQueue>().Should().BeOfType<PostgresDelayQueue>();

        // The stream dispatch service replaces the outbox processor as the read-side mechanism, and
        // the delay-queue processor still drains PM timeouts off the read-model database.
        var hostedServices = services.GetServices<IHostedService>().ToList();
        hostedServices.OfType<DynamoDbStreamDispatchService>().Should().ContainSingle();
        hostedServices.OfType<DelayQueueProcessor>().Should().ContainSingle();

        // No outbox processor: DynamoDB dispatches off its change feed, not an outbox drain, so
        // composing one would drain a table this adapter never writes.
        hostedServices.OfType<OutboxProcessor>().Should().BeEmpty();
    }
}
