using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

public sealed class InvestigationEndToEndTests(AdminConsoleAdmitFixture fixture)
    : IClassFixture<AdminConsoleAdmitFixture>
{
    [Fact]
    public async Task Linked_audit_and_replay_views_read_actual_events_without_changing_persisted_state()
    {
        var orderId = Guid.NewGuid();
        var at = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var order = Order.Draft(orderId, Guid.NewGuid(), at, "test");
        order.AddLine(Guid.NewGuid(), "AUDIT-NOTEBOOK", 2, new Money(12.50m, Currency.USD), at);
        order.SetShippingAddress(new Address("100 Main Street", "Seattle", "98101", "US"), at);
        order.Place(at);
        order.Cancel("Customer requested a later delivery", AdminTestAuthHandler.AdminActorId, at);
        var stream = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderId);
        var correlation = Guid.NewGuid();
        var events = order.DequeueUncommittedEvents().Select((payload, index) =>
        {
            var eventId = Guid.NewGuid();
            return new EventEnvelope(stream, index + 1, eventId, payload.GetType().Name,
                payload is OrderDrafted ? 2 : 1, payload,
                new EventMetadata(eventId, correlation, Guid.NewGuid(), AdminTestAuthHandler.AdminActorId,
                    "investigation-test", at, WellKnownTenants.Default), at, 0);
        }).ToArray();
        await fixture.Factory.Services.GetRequiredService<IEventStore>()
            .AppendAsync(stream, 0, events, CancellationToken.None);
        var before = await FingerprintAsync();
        using var client = fixture.Factory.CreateClient();
        using var history = await client.GetAsync("/order-history?streamId=" + Uri.EscapeDataString(stream.Value));
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        var historyHtml = await history.Content.ReadAsStringAsync();
        historyHtml.Should().Contain("Compare recorded states").And.Contain("Cancelled").And.Contain("AUDIT-NOTEBOOK");
        historyHtml.Should().Contain("Customer requested a later delivery");
        using var audit = await client.GetAsync("/audit?streamId=" + Uri.EscapeDataString(stream.Value));
        audit.StatusCode.Should().Be(HttpStatusCode.OK);
        var auditHtml = await audit.Content.ReadAsStringAsync();
        auditHtml.Should().Contain(nameof(OrderCancelled)).And.Contain(correlation.ToString());
        (await FingerprintAsync()).Should().Be(before, "investigation must not append events or reset read models");
    }

    private async Task<string> FingerprintAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jsonb_build_object(
                'events', (SELECT jsonb_agg(e ORDER BY global_position) FROM event_store.events e),
                'outbox', (SELECT jsonb_agg(o ORDER BY event_id) FROM event_store.outbox o),
                'checkpoints', (SELECT jsonb_agg(c ORDER BY projection_name) FROM read_models.projection_checkpoints c),
                'throughput', (SELECT jsonb_agg(t ORDER BY tenant_id, second_utc) FROM read_models.order_throughput t)
            )::text
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
