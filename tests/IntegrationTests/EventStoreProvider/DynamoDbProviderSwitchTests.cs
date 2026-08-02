using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.EventStoreProvider;

// The DynamoDb provider value, the DynamoDB analog of EventStoreProviderSwitchTests and
// KurrentProviderSwitchTests, against PLAN.md's Phase 2 provider-switch done-when: switching the configured event store is a
// configuration change, not a code change. The host under test is the Api composition every other
// command test boots, the command is the one DraftOrderTests dispatches, and the only difference is
// the provider key and the LocalStack service URL behind it.
//
// This file carries double duty, and the second duty is a residual worth naming. The ruled set wanted
// an Api composition fact separate from the switch fact, and the Api host has nowhere to put one: its
// provider arm lives in top-level Program.cs, not in a factory. WorkersHostFactory.Build exists
// precisely so the Workers arm composes without booting, which is what WorkersDynamoDbCompositionTests
// uses; the Api has no such seam, so booting it is the only way to compose it, and a booted Api is an
// integration fact. Both facts therefore live here, on one fixture. This extends the Phase 13 residual
// the parser facts already carry, where the Api's internal parser twin has no unit home and is covered
// end to end by this file's ancestor.
//
// The fixture sets no EVENT_STORE_CONNECTION_STRING, which is the second thing these facts pin. This
// host does not ask for a DSN on an engine that has none, and a fixture that supplied one anyway
// would boot green whether or not that conditional existed.
public sealed class DynamoDbProviderSwitchTests : IClassFixture<DynamoDbProviderApiFixture>
{
    private readonly DynamoDbProviderApiFixture _fixture;

    public DynamoDbProviderSwitchTests(DynamoDbProviderApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Selecting_the_DynamoDb_provider_appends_the_command_event_to_DynamoDb()
    {
        var client = _fixture.Factory.CreateClient();
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        var response = await client.PostCommandAsync(
            "DraftOrder",
            new { orderId, customerId },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var streamId = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        var eventTypes = await _fixture.ReadEventTypesAsync(streamId.Value);
        eventTypes.Should().ContainSingle().Which.Should().Be("OrderDrafted");

        // The write moved, rather than merely landing. A dual write or a fallback to the PostgreSQL
        // adapter would satisfy the assertion above and fail this one.
        var postgresEventTypes = await _fixture.ReadPostgresEventTypesAsync(streamId.Value);
        postgresEventTypes.Should().BeEmpty();
    }

    // The Api arm's composition evidence: the engine port lands on DynamoDB, and the companion port
    // that DynamoDB does not carry lands on the read-model PostgreSQL database (ADR 0046). The round
    // trip is the second half of the fact on purpose. Resolving PostgresIdempotencyStore proves only
    // that the arm registered a type; a key that records and then reads back proves the store reached
    // the database the read-model key names, which is the part a wrong connection string breaks.
    [Fact]
    public async Task Selecting_the_DynamoDb_provider_composes_a_postgres_idempotency_companion()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<IEventStore>().Should().BeOfType<DynamoDbEventStore>();

        var idempotency = services.GetRequiredService<IIdempotencyStore>();
        idempotency.Should().BeOfType<PostgresIdempotencyStore>();

        var key = Guid.NewGuid().ToString();
        (await idempotency.ExistsAsync(WellKnownTenants.Default, key, CancellationToken.None))
            .Should().BeFalse();
        (await idempotency.TryRecordAsync(
            WellKnownTenants.Default, key, "DraftOrder", CancellationToken.None))
            .Should().BeTrue();
        (await idempotency.ExistsAsync(WellKnownTenants.Default, key, CancellationToken.None))
            .Should().BeTrue();
    }
}
