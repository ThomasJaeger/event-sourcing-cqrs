using Bunit;
using EventSourcingCqrs.Hosts.Web.Components.OrderCreate;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.OrderCreate;

// ShippingAddressStep ships its four address fields and the save control disabled
// in the scaffold; the SetOrderShippingAddress dispatch lands at Commit 25. The
// four fields are free text because Country is consumed only as a display string
// downstream, symmetric with Street, City, and PostalCode.
public sealed class ShippingAddressStepTests : BunitContext
{
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
    public void All_address_inputs_render_disabled()
    {
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.FindAll("input").Should().OnlyContain(i => i.HasAttribute("disabled"));
    }

    [Fact]
    public void Save_button_renders_disabled()
    {
        var cut = Render<ShippingAddressStep>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        var save = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save shipping address");
        save.HasAttribute("disabled").Should().BeTrue();
    }
}
