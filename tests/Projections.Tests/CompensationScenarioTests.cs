using System.Data.Common;
using System.Text.Json;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Demo.Seeder;
using EventSourcingCqrs.Demo.Seeder.Scenarios;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderThroughput;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public sealed class CompensationScenarioTests
{
    [Fact]
    public async Task Completed_compensation_succeeds_when_payment_void_is_after_the_sales_checkpoint()
    {
        var commands = new RecordingCommands();
        var queries = new CompensationQueries(commands, incompleteReads: 0);
        using var provider = CreateProjectionProvider();
        var waiter = new ProjectionCatchUpWaiter(
            new FixedHead(11), new FixedCheckpoint(10), provider, new ExpiringTimeProvider());
        var context = new SeederContext(commands, queries, waiter, new JsonSerializerOptions());

        await CompensationScenario.RunAsync(context, CancellationToken.None);

        queries.CompletedReads.Should().BePositive();
    }

    [Fact]
    public async Task A_cancelled_order_without_its_payment_void_does_not_finish_the_scenario()
    {
        var commands = new RecordingCommands();
        var queries = new CompensationQueries(commands, incompleteReads: 1);
        using var provider = CreateProjectionProvider();
        var waiter = new ProjectionCatchUpWaiter(
            new FixedHead(10), new FixedCheckpoint(10), provider, TimeProvider.System);
        var context = new SeederContext(commands, queries, waiter, new JsonSerializerOptions());

        await CompensationScenario.RunAsync(context, CancellationToken.None);

        queries.CompletedReads.Should().BePositive("the scenario must observe this order's payment void before reporting completion");
    }

    private static ServiceProvider CreateProjectionProvider()
        => new ServiceCollection()
            .AddSingleton<IEventHandler<OrderCancelled>>(
                new OrderThroughputProjection(new InMemoryOrderThroughputStore()))
            .BuildServiceProvider();

    private sealed class RecordingCommands : ICommandBus
    {
        public Guid OrderId { get; private set; }
        public Guid CustomerId { get; private set; }

        public Task SendAsync(ICommand command, CancellationToken ct)
        {
            if (command is DraftOrder drafted)
            {
                OrderId = drafted.OrderId;
                CustomerId = drafted.CustomerId;
            }
            return Task.CompletedTask;
        }

        public Task SendAsync(ICommand command, string? key, CancellationToken ct) => SendAsync(command, ct);
        public Task SendAsync(ICommand command, Guid actor, IReadOnlyCollection<Role> roles,
            TenantId tenant, string? key, CancellationToken ct) => SendAsync(command, ct);
    }

    private sealed class CompensationQueries(RecordingCommands commands, int incompleteReads) : IQueryBus
    {
        private int _reads;
        public int CompletedReads { get; private set; }

        public Task<TResult> AskAsync<TResult>(IQuery<TResult> query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var detail = (GetOrderDetail)(object)query;
            detail.OrderId.Should().Be(commands.OrderId);
            var complete = _reads++ >= incompleteReads;
            if (complete) CompletedReads++;
            var now = DateTime.UtcNow;
            var header = new OrderDetailRow(commands.OrderId, commands.CustomerId, OrderStatus.Cancelled,
                now, null, now, null, null, null, null, now);
            var cancelled = new OrderDetailTimelineRow(commands.OrderId, 10, nameof(OrderCancelled), now, "{}");
            var voided = new OrderDetailTimelineRow(commands.OrderId, 11, nameof(PaymentVoided), now, "{}");
            var view = new OrderDetailView(header, [], complete ? [cancelled, voided] : [cancelled]);
            return Task.FromResult((TResult)(object)view);
        }

        public Task<TResult> AskAsync<TResult>(IQuery<TResult> query, Guid actor,
            IReadOnlyCollection<Role> roles, TenantId tenant, CancellationToken ct) => AskAsync(query, ct);
    }

    private sealed class FixedHead(long position) : IProjectionFeedHeadPosition
    {
        public Task<long> GetFeedHeadPositionAsync(CancellationToken ct) => Task.FromResult(position);
    }

    private sealed class FixedCheckpoint(long position) : ICheckpointStore
    {
        public Task<long> GetPositionAsync(string name, CancellationToken ct) => Task.FromResult(position);
        public Task<long> GetPositionAsync(string name, DbTransaction transaction, CancellationToken ct) => throw new NotSupportedException();
        public Task AdvanceAsync(string name, long value, DbTransaction transaction, CancellationToken ct) => throw new NotSupportedException();
        public Task AdvanceAsync(string name, long value, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ExpiringTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()
        {
            var result = _now;
            _now = _now.AddMinutes(1);
            return result;
        }
    }
}
