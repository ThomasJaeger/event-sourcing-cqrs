using Bunit;
using EventSourcingCqrs.Hosts.Web.Components.Inventory;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.Inventory;

public class AdjustInventoryDialogTests : BunitContext
{
    [Fact]
    public void Dialog_renders_the_current_on_hand()
    {
        var cut = Render<AdjustInventoryDialog>(p => p
            .Add(d => d.Sku, "SKU-1")
            .Add(d => d.CurrentOnHand, 12)
            .Add(d => d.OnSubmit, ((int, string) _) => { })
            .Add(d => d.OnCancel, () => { }));

        cut.Find("dd.font-semibold").TextContent.Trim().Should().Be("12");
    }

    [Fact]
    public void Dialog_renders_delta_and_reason_inputs()
    {
        var cut = Render<AdjustInventoryDialog>(p => p
            .Add(d => d.Sku, "SKU-1")
            .Add(d => d.CurrentOnHand, 0)
            .Add(d => d.OnSubmit, ((int, string) _) => { })
            .Add(d => d.OnCancel, () => { }));

        cut.FindAll("#deltaInput").Should().ContainSingle();
        cut.FindAll("#reasonInput").Should().ContainSingle();
    }

    [Fact]
    public void Adjust_button_is_disabled_when_the_delta_is_zero()
    {
        var cut = Render<AdjustInventoryDialog>(p => p
            .Add(d => d.Sku, "SKU-1")
            .Add(d => d.CurrentOnHand, 0)
            .Add(d => d.OnSubmit, ((int, string) _) => { })
            .Add(d => d.OnCancel, () => { }));

        // Reason present but delta still zero.
        cut.Find("#reasonInput").Input("restock");

        cut.Find("#dialogAdjustButton").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Adjust_button_is_disabled_when_the_reason_is_blank()
    {
        var cut = Render<AdjustInventoryDialog>(p => p
            .Add(d => d.Sku, "SKU-1")
            .Add(d => d.CurrentOnHand, 0)
            .Add(d => d.OnSubmit, ((int, string) _) => { })
            .Add(d => d.OnCancel, () => { }));

        cut.Find("#deltaInput").Input("5");

        cut.Find("#dialogAdjustButton").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Adjust_button_is_enabled_when_delta_and_reason_are_present()
    {
        var cut = Render<AdjustInventoryDialog>(p => p
            .Add(d => d.Sku, "SKU-1")
            .Add(d => d.CurrentOnHand, 0)
            .Add(d => d.OnSubmit, ((int, string) _) => { })
            .Add(d => d.OnCancel, () => { }));

        cut.Find("#deltaInput").Input("5");
        cut.Find("#reasonInput").Input("restock");

        cut.Find("#dialogAdjustButton").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Submitting_raises_OnSubmit_with_the_delta_and_reason()
    {
        (int Delta, string Reason)? captured = null;
        var cut = Render<AdjustInventoryDialog>(p => p
            .Add(d => d.Sku, "SKU-1")
            .Add(d => d.CurrentOnHand, 0)
            .Add(d => d.OnSubmit, ((int Delta, string Reason) t) => captured = t)
            .Add(d => d.OnCancel, () => { }));

        cut.Find("#deltaInput").Input("-3");
        cut.Find("#reasonInput").Input("damaged");
        cut.Find("#dialogAdjustButton").Click();

        captured.Should().Be((-3, "damaged"));
    }

    [Fact]
    public void Cancelling_raises_OnCancel()
    {
        var cancelled = false;
        var cut = Render<AdjustInventoryDialog>(p => p
            .Add(d => d.Sku, "SKU-1")
            .Add(d => d.CurrentOnHand, 0)
            .Add(d => d.OnSubmit, ((int, string) _) => { })
            .Add(d => d.OnCancel, () => cancelled = true));

        cut.Find("#dialogAdjustCancelButton").Click();

        cancelled.Should().BeTrue();
    }
}
