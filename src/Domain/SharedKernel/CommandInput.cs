using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.SharedKernel;

// Validate before raising new events. Historical event constructors stay unchanged
// so replay remains compatible with the existing event corpus (Chapter 11).
internal static class CommandInput
{
    // The shared monetary contract fits the shipped NUMERIC(18,4) read models.
    private const decimal MaximumAmount = 99_999_999_999_999.9999m;

    public static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"{field} must be non-empty.");
        RequireSupportedText(value, field);
    }

    public static void RequireSupportedText(string value, string field)
    {
        // PostgreSQL backs every shipped read model and cannot represent NUL in
        // TEXT or JSONB. Reject it even when the event-store provider accepts it.
        if (value.Contains('\0'))
            throw new DomainException($"{field} must not contain a NUL character.");
    }

    public static void RequireAmount(Money? value, string field)
    {
        if (value?.Currency is null)
            throw new DomainException($"{field} and its currency are required.");
        if (value.Amount < -MaximumAmount || value.Amount > MaximumAmount)
            throw new DomainException($"{field} exceeds the supported monetary range.");
    }

    public static void RequireAddress(Address? value)
    {
        if (value is null)
            throw new DomainException("Shipping address is required.");
        RequireText(value.Street, "Shipping street");
        RequireText(value.City, "Shipping city");
        RequireText(value.PostalCode, "Shipping postal code");
        RequireText(value.Country, "Shipping country");
    }
}
