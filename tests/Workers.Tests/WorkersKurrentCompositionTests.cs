using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.Workers;
using EventSourcingCqrs.Infrastructure.EventStore.Kurrent;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace EventSourcingCqrs.Workers.Tests;

// RED for slice 4's Workers Kurrent composition. Once the subscription dispatch service ships, the
// three loud-throw arms give way to the real Kurrent composition: the event store on KurrentDB, the
// idempotency store and delay queue on the read-model PostgreSQL database, the delay-queue processor
// draining PM timeouts there, and the subscription service in the outbox processor's place. This host
// composes DI only, so placeholder connection strings suffice: no adapter opens a connection until the
// host starts, which this fact never does.
//
// It runs RED today by building the host, which fails at the first loud-throw arm
// (WorkersHostFactory.cs:66) with the "cannot compose the Kurrent event store provider" message; the
// GREEN slice replaces the throws and every assertion below then holds.
public class WorkersKurrentCompositionTests
{
    private const string KurrentConnectionString = "esdb://localhost:2113?tls=false";
    private const string ReadModelConnectionString = "Host=localhost;Database=stub";

    [Fact]
    public void Building_the_Kurrent_arm_composes_the_write_side_and_the_subscription_dispatch_service()
    {
        using var host = WorkersHostFactory.Build(
            EventStoreProvider.Kurrent, KurrentConnectionString, ReadModelConnectionString);
        var services = host.Services;

        // The event store is KurrentDB; the companion ports land on the read-model PostgreSQL database.
        services.GetRequiredService<IEventStore>().Should().BeOfType<KurrentEventStore>();
        services.GetRequiredService<IIdempotencyStore>().Should().BeOfType<PostgresIdempotencyStore>();
        services.GetRequiredService<IDelayQueue>().Should().BeOfType<PostgresDelayQueue>();

        // The subscription service replaces the outbox processor as the read-side dispatch mechanism,
        // and the delay-queue processor still drains PM timeouts off the read-model database.
        var hostedServices = services.GetServices<IHostedService>().ToList();
        hostedServices.OfType<KurrentSubscriptionService>().Should().ContainSingle();
        hostedServices.OfType<DelayQueueProcessor>().Should().ContainSingle();

        // No outbox processor: KurrentDB dispatches through a catch-up subscription, not an outbox
        // drain, so composing one would double-dispatch or drain an outbox the Kurrent adapter never
        // writes to.
        hostedServices.OfType<OutboxProcessor>().Should().BeEmpty();
    }
}
