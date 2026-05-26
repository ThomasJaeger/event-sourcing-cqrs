using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.OrderCreate;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// ShippingAddressStep is enabled at Commit 25: it dispatches SetOrderShippingAddress
// (step-owned, like CustomerStep), gates Save on all four fields being non-empty
// (design call G.b), and rehydrates the form on back-navigation. These tests pin the
// gating, the dispatch payload, the OnAddressChanged-then-OnContinue ordering, the
// four failure arms, and the rehydrate.
public sealed class ShippingAddressStepTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public ShippingAddressStepTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
    }

    [Fact]
    public void Renders_the_four_address_fields_as_text_inputs()
    {
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Markup.Should().Contain("Street");
        cut.Markup.Should().Contain("City");
        cut.Markup.Should().Contain("Postal code");
        cut.Markup.Should().Contain("Country");
        cut.FindAll("input[type=text]").Should().HaveCount(4);
    }

    [Fact]
    public void All_address_inputs_render_enabled_when_not_dispatching()
    {
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.FindAll("input").Should().OnlyContain(i => !i.HasAttribute("disabled"));
    }

    [Fact]
    public void Save_is_disabled_when_any_field_is_empty()
    {
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#streetInput").Change("1 Main St");
        cut.Find("#cityInput").Change("Springfield");
        cut.Find("#postalCodeInput").Change("12345");
        // Country left empty.

        cut.Find("button").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Save_is_enabled_when_all_fields_are_populated()
    {
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Find("#streetInput").Change("1 Main St");
        cut.Find("#cityInput").Change("Springfield");
        cut.Find("#postalCodeInput").Change("12345");
        cut.Find("#countryInput").Change("USA");

        cut.Find("button").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Submit_dispatches_SetOrderShippingAddress_with_the_populated_address()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueCommandResult(typeof(SetOrderShippingAddress), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, orderId));

        FillAddress(cut);
        cut.Find("button").Click();

        var dispatched = stubApiClient.CapturedCommands.Should().ContainSingle().Subject.Command
            .Should().BeOfType<SetOrderShippingAddress>().Subject;
        dispatched.OrderId.Should().Be(orderId);
        dispatched.ShippingAddress.Street.Should().Be("1 Main St");
        dispatched.ShippingAddress.City.Should().Be("Springfield");
        dispatched.ShippingAddress.PostalCode.Should().Be("12345");
        dispatched.ShippingAddress.Country.Should().Be("USA");
    }

    [Fact]
    public void Submit_raises_OnAddressChanged_then_OnContinue()
    {
        var calls = new List<string>();
        stubApiClient.EnqueueCommandResult(typeof(SetOrderShippingAddress), new CommandAcceptedResponse(DateTime.UtcNow));
        var cut = Render<ShippingAddressStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.OnAddressChanged, (Address _) => calls.Add("changed"))
            .Add(x => x.OnContinue, () => calls.Add("continue")));

        FillAddress(cut);
        cut.Find("button").Click();

        calls.Should().Equal("changed", "continue");
    }

    [Fact]
    public void Validation_failure_renders_the_failure_message()
    {
        stubApiClient.SeedCommandFailure<SetOrderShippingAddress>(new ApiValidationException(
            new Dictionary<string, IReadOnlyList<string>> { ["Street"] = new[] { "Street is required" } }));
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        FillAddress(cut);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("Street is required");
    }

    [Fact]
    public void Business_rule_failure_renders_the_failure_message()
    {
        stubApiClient.SeedCommandFailure<SetOrderShippingAddress>(new ApiBusinessRuleException(
            "RULE", "The address was rejected."));
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        FillAddress(cut);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("The address was rejected.");
    }

    [Fact]
    public void Concurrency_failure_renders_the_concurrency_message()
    {
        stubApiClient.SeedCommandFailure<SetOrderShippingAddress>(new ApiConcurrencyException(0));
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        FillAddress(cut);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("The order changed while you were editing");
    }

    [Fact]
    public void Infrastructure_failure_renders_the_infrastructure_message()
    {
        stubApiClient.SeedCommandFailure<SetOrderShippingAddress>(
            new ApiInfrastructureException("connection refused", statusCode: 500));
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        FillAddress(cut);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("could not be saved");
        cut.Markup.Should().Contain("connection refused");
    }

    [Fact]
    public void Rehydrates_the_form_from_an_existing_address()
    {
        var cut = Render<ShippingAddressStep>(p => p
            .Add(x => x.OrderId, Guid.NewGuid())
            .Add(x => x.Address, new Address("1 Main St", "Springfield", "12345", "USA")));

        cut.Find("#streetInput").GetAttribute("value").Should().Be("1 Main St");
        cut.Find("#cityInput").GetAttribute("value").Should().Be("Springfield");
        cut.Find("#postalCodeInput").GetAttribute("value").Should().Be("12345");
        cut.Find("#countryInput").GetAttribute("value").Should().Be("USA");
    }

    private static void FillAddress(IRenderedComponent<ShippingAddressStep> cut)
    {
        cut.Find("#streetInput").Change("1 Main St");
        cut.Find("#cityInput").Change("Springfield");
        cut.Find("#postalCodeInput").Change("12345");
        cut.Find("#countryInput").Change("USA");
    }
}
