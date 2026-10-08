using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Authentication;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

public class OrderHistoryPageTests : BunitContext
{
    private static readonly Guid OrderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CustomerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private readonly HistoryClient client = new();

    public OrderHistoryPageTests() => Services.AddSingleton<IApiClient>(client);

    [Fact]
    public void History_compares_the_last_two_recorded_steps_without_command_controls()
    {
        client.Results.Enqueue(History());
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        client.OrderIds.Should().Equal(OrderId);
        cut.Find("select#earlier-step").GetAttribute("value").Should().Be("3");
        cut.Find("select#later-step").GetAttribute("value").Should().Be("4");
        cut.Find("[aria-label='Earlier order details']").TextContent.Should().Contain("Placed").And.Contain("NOTEBOOK").And.Contain("42 Cedar Lane");
        cut.Find("[aria-label='Later order details']").TextContent.Should().Contain("Cancelled").And.Contain("$25.00");
        cut.Find("[aria-label='Changes between steps']").TextContent.Should().Contain("Status changed from Placed to Cancelled");
        cut.Markup.Should().Contain("Payment and delivery");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Refresh history");
        client.CommandCount.Should().Be(0);
    }

    [Fact]
    public void Choosing_steps_compares_added_items_address_and_total_from_that_same_capture()
    {
        client.Results.Enqueue(History());
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("select#earlier-step").Change("1");
        cut.Find("select#later-step").Change("3");
        var changes = cut.Find("[aria-label='Changes between steps']").TextContent;
        changes.Should().Contain("Added 2 × NOTEBOOK").And.Contain("$12.50")
            .And.Contain("Shipping address").And.Contain("42 Cedar Lane")
            .And.Contain("$0.00").And.Contain("$25.00");
        client.OrderIds.Should().ContainSingle("selection compares one captured history without more queries");
    }

    [Fact]
    public void Comparison_distinguishes_removing_an_item_from_adding_an_item_with_the_same_sku()
    {
        var original = History().Steps[1];
        var replacement = original with
        {
            Version = 3,
            Description = "Line replaced",
            Snapshot = original.Snapshot with { Lines = [new(Guid.NewGuid(), "NOTEBOOK", 1, new Money(15m, Currency.USD))] },
            Total = new Money(15m, Currency.USD),
        };
        client.Results.Enqueue(new OrderHistoryView(OrderId, [original, replacement]));
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("[aria-label='Changes between steps']").TextContent.Should()
            .Contain("Removed 2 × NOTEBOOK").And.Contain("Added 1 × NOTEBOOK");
    }

    [Fact]
    public void Selecting_the_same_step_reports_no_changes_and_keeps_comparison_order_valid()
    {
        client.Results.Enqueue(History());
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("select#earlier-step").Change("4");
        cut.Find("[aria-label='Changes between steps']").TextContent.Should().Contain("No order details changed");
        cut.Find("select#later-step").Change("1");
        cut.Find("select#later-step").GetAttribute("value").Should().Be("4");
    }

    [Fact]
    public void A_single_recorded_step_is_readable_without_inventing_an_earlier_state()
    {
        client.Results.Enqueue(new OrderHistoryView(OrderId, [History().Steps[0]]));
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("select#earlier-step").GetAttribute("value").Should().Be("1");
        cut.Find("select#later-step").GetAttribute("value").Should().Be("1");
        cut.Find("[aria-label='Earlier order details']").TextContent.Should().Contain("No items").And.Contain("Not set");
    }

    [Fact]
    public void Refresh_recaptures_the_history_and_selects_the_new_last_two_steps()
    {
        client.Results.Enqueue(new OrderHistoryView(OrderId, History().Steps.Take(3).ToArray()));
        client.Results.Enqueue(History());
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("button").Click();
        client.OrderIds.Should().Equal(OrderId, OrderId);
        cut.Find("select#later-step").GetAttribute("value").Should().Be("4");
        cut.Find("[aria-label='Later order details']").TextContent.Should().Contain("Cancelled");
    }

    [Fact]
    public void Missing_or_hidden_orders_show_no_history()
    {
        client.Results.Enqueue(null);
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Markup.Should().Contain("Order not found");
        cut.FindAll("select").Should().BeEmpty();
    }

    [Fact]
    public void An_empty_history_has_a_clear_state()
    {
        client.Results.Enqueue(new OrderHistoryView(OrderId, []));
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Markup.Should().Contain("No recorded changes");
        cut.FindAll("select").Should().BeEmpty();
    }

    [Fact]
    public void Anonymous_history_access_offers_sign_in_with_a_full_navigation_return_to_this_order()
    {
        client.Failure = new ForwardedIdentityUnavailableException();
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("[role='status']").TextContent.Should().Contain("Sign in to view order history");
        var expected = "/login?ReturnUrl=" + Uri.EscapeDataString($"/orders/{OrderId}/history");
        var link = cut.Find($"a[href='{expected}']");
        link.TextContent.Should().Contain("Sign in");
        link.GetAttribute("data-enhance-nav").Should().Be("false");
        cut.FindAll("select, [role='alert']").Should().BeEmpty();
    }

    [Fact]
    public void Denied_history_never_renders_recorded_order_details()
    {
        client.Failure = new ApiAuthorizationException("Denied");
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Markup.Should().Contain("You do not have permission to view this order history");
        cut.FindAll("select, [aria-label='Earlier order details']").Should().BeEmpty();
    }

    [Theory]
    [InlineData("HISTORY_UNAVAILABLE", "not available")]
    [InlineData("HISTORY_TOO_LONG", "too many recorded changes")]
    [InlineData("HISTORY_INCOMPLETE", "cannot be reconstructed completely")]
    public void Known_failures_have_helpful_messages_without_exposing_server_details(string code, string expected)
    {
        client.Failure = code == "HISTORY_UNAVAILABLE"
            ? new ApiInfrastructureException("private-server-detail", 501, code)
            : new ApiBusinessRuleException(code, "private-server-detail");
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("[role='alert']").TextContent.Should().Contain(expected).And.NotContain("private-server-detail");
        cut.FindAll("select").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_refresh_hides_old_details_and_retry_can_load_a_fresh_history()
    {
        client.Results.Enqueue(History());
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        client.Failure = new InvalidOperationException("private-server-detail");
        cut.Find("button").Click();
        cut.Find("[role='alert']").TextContent.Should().Contain("Unable to load order history").And.NotContain("private-server-detail");
        cut.FindAll("select").Should().BeEmpty();
        client.Failure = null;
        client.Results.Enqueue(History());
        cut.Find("button").Click();
        cut.FindAll("select").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_pending_read_is_cancelled_on_disposal_and_cannot_expose_late_results()
    {
        client.Pending = new TaskCompletionSource<OrderHistoryView?>();
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        cut.Find("[role='status']").TextContent.Should().Contain("Loading");
        cut.Find("button").HasAttribute("disabled").Should().BeTrue();
        await DisposeAsync();
        client.Tokens.Single().IsCancellationRequested.Should().BeTrue();
        client.Pending.SetResult(History());
    }

    [Fact]
    public void Changing_order_routes_loads_the_new_id_and_discards_the_previous_capture()
    {
        client.Results.Enqueue(History());
        var cut = Render<OrderHistory>(p => p.Add(x => x.OrderId, OrderId));
        var nextId = Guid.NewGuid();
        client.Results.Enqueue(null);
        cut.Render(p => p.Add(x => x.OrderId, nextId));
        client.OrderIds.Should().Equal(OrderId, nextId);
        cut.FindAll("select").Should().BeEmpty();
    }

    private static OrderHistoryView History()
    {
        var lines = new[] { new OrderLine(Guid.Parse("55555555-5555-5555-5555-555555555555"), "NOTEBOOK", 2, new Money(12.50m, Currency.USD)) };
        var address = new Address("42 Cedar Lane", "Seattle", "98101", "USA");
        var start = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        return new(OrderId,
        [
            new(1, start, "Order draft created", new(OrderId, CustomerId, OrderStatus.Draft, [], null), Money.Zero(Currency.USD)),
            new(2, start.AddMinutes(1), "Item added", new(OrderId, CustomerId, OrderStatus.Draft, lines, null), new Money(25m, Currency.USD)),
            new(3, start.AddMinutes(2), "Order placed", new(OrderId, CustomerId, OrderStatus.Placed, lines, address), new Money(25m, Currency.USD)),
            new(4, start.AddMinutes(3), "Order cancelled: plans changed", new(OrderId, CustomerId, OrderStatus.Cancelled, lines, address), new Money(25m, Currency.USD)),
        ]);
    }

    private sealed class HistoryClient : IApiClient
    {
        public Queue<OrderHistoryView?> Results { get; } = new();
        public List<Guid> OrderIds { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Exception? Failure { get; set; }
        public TaskCompletionSource<OrderHistoryView?>? Pending { get; set; }
        public int CommandCount { get; private set; }
        public async Task<TResult?> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct)
        {
            var historyQuery = query.Should().BeOfType<GetOrderHistory>().Subject;
            OrderIds.Add(historyQuery.OrderId);
            Tokens.Add(ct);
            if (Failure is not null) throw Failure;
            var result = Pending is null ? Results.Dequeue() : await Pending.Task;
            return (TResult?)(object?)result;
        }
        public Task<CommandAcceptedResponse> SendCommandAsync(ICommand command, string idempotencyKey, CancellationToken ct)
        {
            CommandCount++;
            throw new InvalidOperationException("History must not dispatch commands.");
        }
    }
}
