using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Domain.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Billing;

public class PaymentTests
{
    private static readonly Guid PaymentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string PmRef = "PM-REF-9001";
    private static readonly Money Amount = new(50m, Currency.USD);
    private static readonly DateTime At = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Authorize_creates_a_payment_at_amount_against_a_payment_method_for_an_order()
    {
        var payment = Payment.Authorize(PaymentId, OrderId, Amount, PmRef, At);

        payment.Id.Should().Be(PaymentId);
        payment.OrderId.Should().Be(OrderId);
        payment.AuthorizedAmount.Should().Be(Amount);
        payment.Status.Should().Be(PaymentStatus.Authorized);
        payment.DequeueUncommittedEvents()
            .Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At));
    }

    [Fact]
    public void Authorize_throws_when_amount_is_negative()
    {
        var negative = new Money(-1m, Currency.USD);
        var action = () => Payment.Authorize(PaymentId, OrderId, negative, PmRef, At);

        action.Should().Throw<DomainException>()
            .WithMessage("*amount must be strictly positive*");
    }

    [Fact]
    public void Authorize_throws_when_amount_is_zero()
    {
        var zero = Money.Zero(Currency.USD);
        var action = () => Payment.Authorize(PaymentId, OrderId, zero, PmRef, At);

        action.Should().Throw<DomainException>()
            .WithMessage("*amount must be strictly positive*");
    }

    [Fact]
    public void Authorize_throws_when_payment_method_reference_is_empty()
    {
        var action = () => Payment.Authorize(PaymentId, OrderId, Amount, "", At);

        action.Should().Throw<DomainException>()
            .WithMessage("*payment method reference must be non-empty*");
    }

    [Fact]
    public void Capture_transitions_an_authorized_payment_to_captured()
    {
        new AggregateTest<Payment>()
            .Given(new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At))
            .When(p => p.Capture(At))
            .Then(new PaymentCaptured(PaymentId, Amount, At));
    }

    [Fact]
    public void Capture_records_the_captured_amount_on_the_aggregate()
    {
        var payment = Payment.Authorize(PaymentId, OrderId, Amount, PmRef, At);
        payment.Capture(At);

        payment.CapturedAmount.Should().Be(Amount);
        payment.Status.Should().Be(PaymentStatus.Captured);
    }

    [Fact]
    public void Capture_throws_when_payment_is_already_captured()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentCaptured(PaymentId, Amount, At))
            .When(p => p.Capture(At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Captured*");
    }

    [Fact]
    public void Capture_throws_when_payment_is_refunded()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentCaptured(PaymentId, Amount, At),
                new PaymentRefunded(PaymentId, Amount, "Customer request", At))
            .When(p => p.Capture(At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Refunded*");
    }

    [Fact]
    public void Capture_throws_when_payment_is_voided()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentVoided(PaymentId, "Cancelled before capture", At))
            .When(p => p.Capture(At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Voided*");
    }

    [Fact]
    public void Refund_transitions_a_captured_payment_to_refunded()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentCaptured(PaymentId, Amount, At))
            .When(p => p.Refund("Customer request", At))
            .Then(new PaymentRefunded(PaymentId, Amount, "Customer request", At));
    }

    [Fact]
    public void Refund_throws_when_reason_is_empty()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentCaptured(PaymentId, Amount, At))
            .When(p => p.Refund("", At))
            .ThenThrows<DomainException>()
            .WithMessage("*reason must be non-empty*");
    }

    [Fact]
    public void Refund_throws_when_payment_is_still_authorized()
    {
        new AggregateTest<Payment>()
            .Given(new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At))
            .When(p => p.Refund("Customer request", At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Authorized*");
    }

    [Fact]
    public void Refund_throws_when_payment_is_already_refunded()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentCaptured(PaymentId, Amount, At),
                new PaymentRefunded(PaymentId, Amount, "First refund", At))
            .When(p => p.Refund("Second refund", At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Refunded*");
    }

    [Fact]
    public void Refund_throws_when_payment_is_voided()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentVoided(PaymentId, "Cancelled before capture", At))
            .When(p => p.Refund("Customer request", At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Voided*");
    }

    [Fact]
    public void Void_transitions_an_authorized_payment_to_voided()
    {
        new AggregateTest<Payment>()
            .Given(new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At))
            .When(p => p.Void("Cancelled before capture", At))
            .Then(new PaymentVoided(PaymentId, "Cancelled before capture", At));
    }

    [Fact]
    public void Void_throws_when_reason_is_empty()
    {
        new AggregateTest<Payment>()
            .Given(new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At))
            .When(p => p.Void("", At))
            .ThenThrows<DomainException>()
            .WithMessage("*reason must be non-empty*");
    }

    [Fact]
    public void Void_throws_when_payment_is_captured()
    {
        // Captured-cannot-be-Voided: once money has moved, the reversal path
        // is Refund, not Void. Matches real payment-processor semantics.
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentCaptured(PaymentId, Amount, At))
            .When(p => p.Void("Attempted void after capture", At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Captured*");
    }

    [Fact]
    public void Void_throws_when_payment_is_already_voided()
    {
        new AggregateTest<Payment>()
            .Given(
                new PaymentAuthorized(PaymentId, OrderId, Amount, PmRef, At),
                new PaymentVoided(PaymentId, "First void", At))
            .When(p => p.Void("Second void", At))
            .ThenThrows<DomainException>()
            .WithMessage("*payment is Voided*");
    }

    [Fact]
    public void Lifecycle_capture_path_authorize_then_capture_then_refund()
    {
        var payment = Payment.Authorize(PaymentId, OrderId, Amount, PmRef, At);
        payment.Capture(At);
        payment.Refund("Customer request", At);

        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.DequeueUncommittedEvents().Should().HaveCount(3);
    }

    [Fact]
    public void Lifecycle_void_path_authorize_then_void()
    {
        var payment = Payment.Authorize(PaymentId, OrderId, Amount, PmRef, At);
        payment.Void("Cancelled before capture", At);

        payment.Status.Should().Be(PaymentStatus.Voided);
        payment.DequeueUncommittedEvents().Should().HaveCount(2);
    }
}
