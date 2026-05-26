using Bunit;
using EventSourcingCqrs.Hosts.Web.Components.Inventory;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components.Inventory;

public class CreateInventoryDialogTests : BunitContext
{
    [Fact]
    public void Dialog_renders_a_sku_input()
    {
        var cut = Render<CreateInventoryDialog>(p => p
            .Add(d => d.OnSubmit, (string _) => { })
            .Add(d => d.OnCancel, () => { }));

        cut.FindAll("#skuInput").Should().ContainSingle();
    }

    [Fact]
    public void Create_button_is_disabled_when_the_sku_is_blank()
    {
        var cut = Render<CreateInventoryDialog>(p => p
            .Add(d => d.OnSubmit, (string _) => { })
            .Add(d => d.OnCancel, () => { }));

        cut.Find("#dialogCreateButton").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Create_button_is_enabled_once_a_sku_is_entered()
    {
        var cut = Render<CreateInventoryDialog>(p => p
            .Add(d => d.OnSubmit, (string _) => { })
            .Add(d => d.OnCancel, () => { }));

        cut.Find("#skuInput").Input("SKU-1");

        cut.Find("#dialogCreateButton").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Submitting_raises_OnSubmit_with_the_entered_sku()
    {
        string? captured = null;
        var cut = Render<CreateInventoryDialog>(p => p
            .Add(d => d.OnSubmit, (string s) => captured = s)
            .Add(d => d.OnCancel, () => { }));

        cut.Find("#skuInput").Input("SKU-9");
        cut.Find("#dialogCreateButton").Click();

        captured.Should().Be("SKU-9");
    }

    [Fact]
    public void Cancelling_raises_OnCancel()
    {
        var cancelled = false;
        var cut = Render<CreateInventoryDialog>(p => p
            .Add(d => d.OnSubmit, (string _) => { })
            .Add(d => d.OnCancel, () => cancelled = true));

        cut.Find("#dialogCancelButton").Click();

        cancelled.Should().BeTrue();
    }
}
