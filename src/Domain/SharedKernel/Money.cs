using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.SharedKernel;

public sealed record Money(decimal Amount, Currency Currency)
{
    public static Money Zero(Currency currency) => new(0m, currency);

    public bool IsNegative => Amount < 0m;
    public bool IsZero => Amount == 0m;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    // Scalar multiplication rounds to the currency's precision using banker's rounding.
    // The rule lives at every operation site, including OrderLine.Subtotal.
    public static Money operator *(Money money, decimal scalar)
    {
        var product = money.Amount * scalar;
        var rounded = Math.Round(product, money.Currency.DecimalPlaces, MidpointRounding.ToEven);
        return new Money(rounded, money.Currency);
    }

    public static bool operator <(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount < right.Amount;
    }

    public static bool operator >(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount > right.Amount;
    }

    public static bool operator <=(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount <= right.Amount;
    }

    public static bool operator >=(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return left.Amount >= right.Amount;
    }

    // Inputs with sub-currency-precision are rounded to currency precision at
    // allocation time using banker's rounding. The returned Money values are
    // always at currency precision and sum to a currency-precision
    // representation of the original.
    public Money[] Allocate(int n)
    {
        if (n <= 0)
        {
            throw new DomainException(
                $"Cannot allocate Money into {n} parts: n must be positive.");
        }

        var scale = Pow10(Currency.DecimalPlaces);
        var totalMinor = (long)Math.Round(Amount * scale, 0, MidpointRounding.ToEven);
        var baseMinor = totalMinor / n;
        var remainder = (int)(totalMinor - baseMinor * n);

        var result = new Money[n];
        for (var i = 0; i < n; i++)
        {
            var minor = baseMinor + (i < remainder ? 1 : 0);
            result[i] = new Money(minor / scale, Currency);
        }
        return result;
    }

    public Money[] Allocate(int[] ratios)
    {
        if (ratios is null || ratios.Length == 0)
        {
            throw new DomainException("Cannot allocate Money with no ratios.");
        }
        var total = 0;
        foreach (var r in ratios)
        {
            if (r < 0)
            {
                throw new DomainException(
                    $"Cannot allocate Money with negative ratio {r}.");
            }
            total += r;
        }
        if (total == 0)
        {
            throw new DomainException("Cannot allocate Money with ratios that sum to zero.");
        }

        var scale = Pow10(Currency.DecimalPlaces);
        var totalMinor = (long)Math.Round(Amount * scale, 0, MidpointRounding.ToEven);

        var result = new Money[ratios.Length];
        long allocated = 0;
        for (var i = 0; i < ratios.Length; i++)
        {
            // Multiply in decimal to keep large-amount-times-large-ratio products
            // out of long-overflow range. The cast back to long truncates toward
            // zero, matching the integer-division semantics this loop relies on.
            var share = (long)((decimal)totalMinor * ratios[i] / total);
            result[i] = new Money(share / scale, Currency);
            allocated += share;
        }

        var remainder = (int)(totalMinor - allocated);
        for (var i = 0; i < remainder; i++)
        {
            result[i] = new Money(result[i].Amount + 1m / scale, Currency);
        }
        return result;
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new DomainException(
                $"Cannot operate on {left.Currency.Code} and {right.Currency.Code}: currencies must match.");
        }
    }

    // Computes 10^n as a decimal. Math.Pow returns double, and round-tripping
    // through double introduces precision loss that breaks banker's rounding
    // on currency-precision boundaries.
    private static decimal Pow10(int n)
    {
        decimal result = 1m;
        for (var i = 0; i < n; i++)
        {
            result *= 10m;
        }
        return result;
    }
}
