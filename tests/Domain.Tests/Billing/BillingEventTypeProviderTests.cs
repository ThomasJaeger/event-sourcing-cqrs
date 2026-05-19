using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Billing.Events;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Billing;

public class BillingEventTypeProviderTests
{
    [Fact]
    public void GetEventTypes_returns_all_Billing_events_in_canonical_lifecycle_order()
    {
        var provider = new BillingEventTypeProvider();

        provider.GetEventTypes().Should().Equal(
            typeof(PaymentAuthorized),
            typeof(PaymentCaptured),
            typeof(PaymentRefunded),
            typeof(PaymentVoided));
    }
}
