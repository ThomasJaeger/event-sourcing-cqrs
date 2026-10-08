using Bunit;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.AdminConsole.Browser;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

public class OrderReplayLabTests : BunitContext
{
    private const string Stream = "order:11111111111111111111111111111111";

    [Fact]
    public void Comparing_versions_uses_one_captured_history_and_never_calls_a_writer()
    {
        var reader = new StubLab();
        Services.AddSingleton<IOrderReplayLab>(reader);
        var cut = Render<OrderReplayLab>();
        cut.Find("input").Change(Stream);
        cut.Find("button[data-load-history]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-order-state]").Should().HaveCount(2));
        cut.Find("[data-order-state='before']").TextContent.Should().Contain("Draft");
        cut.Find("[data-order-state='after']").TextContent.Should().Contain("Cancelled");
        cut.Find("select#before-version").Change("2");
        cut.Find("[data-order-state='before']").TextContent.Should().Contain("Placed");
        cut.Find("[data-comparison]").TextContent.Should().Contain("Placed").And.Contain("Cancelled");
        reader.Calls.Should().Be(1);
        cut.FindAll("a").Select(a => a.GetAttribute("href")).Should().Contain("/audit?streamId=" + Uri.EscapeDataString(Stream));
        cut.Find("button[data-load-history]").Click();
        reader.Calls.Should().Be(2);
    }

    [Fact]
    public void Invalid_or_missing_stream_clears_previous_states_and_explains_the_result()
    {
        var reader = new StubLab();
        Services.AddSingleton<IOrderReplayLab>(reader);
        var cut = Render<OrderReplayLab>();
        cut.Find("input").Change(Stream);
        cut.Find("button[data-load-history]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-order-state]").Should().HaveCount(2));
        reader.Result = new(null, "No order events were found for this stream.");
        cut.Find("input").Change("order:22222222222222222222222222222222");
        cut.Find("button[data-load-history]").Click();
        cut.FindAll("[data-order-state]").Should().BeEmpty();
        cut.Find("[role='status']").TextContent.Should().Contain("No order events");
    }

    [Fact]
    public void Reader_failure_is_visible_without_disclosing_exception_details()
    {
        Services.AddSingleton<IOrderReplayLab>(new FailingLab());
        var cut = Render<OrderReplayLab>();
        cut.Find("input").Change(Stream);
        cut.Find("button[data-load-history]").Click();
        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.Should().Contain("Unable"));
        cut.Markup.Should().NotContain("private database details");
    }

    private sealed class FailingLab : IOrderReplayLab
    {
        public Task<OrderReplayResult> ReadAsync(string streamId, CancellationToken ct)
            => throw new InvalidOperationException("private database details");
    }

    private sealed class StubLab : IOrderReplayLab
    {
        public int Calls { get; private set; }
        public OrderReplayResult Result { get; set; } = new(new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        [
            Step(1, OrderStatus.Draft, "Order drafted"),
            Step(2, OrderStatus.Placed, "Order placed"),
            Step(3, OrderStatus.Cancelled, "Order cancelled: customer changed their mind")
        ]), null);
        public Task<OrderReplayResult> ReadAsync(string streamId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
        private static OrderHistoryStep Step(int version, OrderStatus status, string description)
            => new(version, new DateTime(2026, 10, 7, 10, version, 0, DateTimeKind.Utc), description,
                new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.NewGuid(), status, [], null),
                Money.Zero(Currency.USD));
    }
}
