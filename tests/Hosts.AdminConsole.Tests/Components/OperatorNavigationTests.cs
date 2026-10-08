using Bunit;
using EventSourcingCqrs.Hosts.AdminConsole.Components;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

public class OperatorNavigationTests : BunitContext
{
    [Fact]
    public void Home_links_to_each_existing_operator_tool()
    {
        Services.AddSingleton(CorrelationTracerAvailability.Available);
        var cut = Render<Home>();
        cut.FindAll("a").Select(a => a.GetAttribute("href"))
            .Should().BeEquivalentTo(new[] { "/audit", "/order-history", "/streams", "/correlations", "/projections", "/replay" });
    }

    [Fact]
    public void Routes_supply_shared_navigation_and_a_keyboard_skip_target()
    {
        Services.AddSingleton(CorrelationTracerAvailability.Available);
        var cut = Render<Routes>();
        var navigation = cut.Find("nav[aria-label='Operator tools']");
        navigation.QuerySelectorAll("a").Select(a => a.GetAttribute("href"))
            .Should().BeEquivalentTo(new[] { "/", "/audit", "/order-history", "/streams", "/correlations", "/projections", "/replay" });
        cut.Find("a[href='#main-content']").TextContent.Should().Contain("Skip to content");
        cut.Find("main#main-content").GetAttribute("tabindex").Should().Be("-1");
        cut.Find("header a[href='/login']").TextContent.Should().Contain("Account");
        navigation.QuerySelectorAll("a[aria-current='page']").Should().ContainSingle()
            .Which.GetAttribute("href").Should().Be("/");
    }

    [Fact]
    public void Home_explains_when_correlation_tracing_is_unavailable()
    {
        Services.AddSingleton(CorrelationTracerAvailability.Unavailable("No cross-stream index."));
        var cut = Render<Home>();
        cut.Markup.Should().Contain("No cross-stream index.");
        cut.Find("a[href='/correlations']").TextContent.Should().Contain("availability");
    }
}
