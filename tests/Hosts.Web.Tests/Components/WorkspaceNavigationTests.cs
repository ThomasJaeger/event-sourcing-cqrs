using Bunit;
using EventSourcingCqrs.Hosts.Web.Components.Layout;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

public class WorkspaceNavigationTests : BunitContext
{
    [Fact]
    public void Layout_offers_the_workspace_routes_and_a_skip_target()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<h1>Current page</h1>")));

        cut.FindAll("nav[aria-label='Primary'] a").Select(link => link.GetAttribute("href"))
            .Should().Equal("/", "/orders", "/my-orders", "/inventory", "/admin/throughput");
        cut.Find("a[href='#main-content']").TextContent.Trim().Should().Be("Skip to content");
        cut.Find("main#main-content").GetAttribute("tabindex").Should().Be("-1");
        cut.Find("main h1").TextContent.Should().Be("Current page");
        cut.Find("header a[href='/orders/new']").Should().NotBeNull();
        cut.Find("header a[href='/login']").Should().NotBeNull();
    }

    [Fact]
    public void Navigation_marks_orders_active_on_an_order_page_without_marking_overview_active()
    {
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/orders/6f1eb5d7-86bb-49ba-9c58-c03a582204a7");

        var cut = Render<MainLayout>();

        cut.Find("nav a[href='/orders']").ClassList.Should().Contain("font-semibold");
        cut.Find("nav a[href='/']").ClassList.Should().NotContain("font-semibold");
    }

    [Fact]
    public void Home_tasks_lead_to_existing_workspace_pages()
    {
        var cut = Render<Home>();

        cut.FindAll("a").Select(link => link.GetAttribute("href")).Distinct()
            .Should().BeEquivalentTo("/orders/new", "/orders", "/my-orders", "/inventory", "/admin/throughput");
        cut.FindAll("h1").Should().ContainSingle();
    }
}
