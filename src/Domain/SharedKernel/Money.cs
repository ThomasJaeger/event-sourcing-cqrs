namespace EventSourcingCqrs.Domain.SharedKernel;

public sealed record Money(decimal Amount, string Currency)
{
    public static Money Zero { get; } = new(0m, "");

    public static Money operator +(Money left, Money right)
    {
        if (left.Currency == "")
        {
            return right;
        }
        if (right.Currency == "")
        {
            return left;
        }
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException(
                $"Cannot operate on {left.Currency} and {right.Currency}: currencies must match.");
        }
    }
}
