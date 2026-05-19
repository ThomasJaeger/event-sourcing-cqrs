using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.SharedKernel;

public sealed record Currency
{
    private static readonly IReadOnlyDictionary<string, int> PrecisionTable = new Dictionary<string, int>
    {
        ["USD"] = 2,
        ["EUR"] = 2,
        ["GBP"] = 2,
        ["JPY"] = 0,
        ["BHD"] = 3,
    };

    public static Currency USD { get; } = new("USD");
    public static Currency EUR { get; } = new("EUR");
    public static Currency GBP { get; } = new("GBP");
    public static Currency JPY { get; } = new("JPY");

    public string Code { get; }

    public Currency(string code)
    {
        if (code is null || code.Length != 3 || !IsAllUppercaseAscii(code))
        {
            throw new DomainException(
                $"Currency code '{code}' is not a valid ISO-4217 three-letter uppercase code.");
        }
        Code = code;
    }

    // Looked up at access time so the type carries one field (Code) and equality
    // discriminates on Code alone. Unknown valid-format codes default to 2.
    public int DecimalPlaces =>
        PrecisionTable.TryGetValue(Code, out var places) ? places : 2;

    private static bool IsAllUppercaseAscii(string code)
    {
        foreach (var ch in code)
        {
            if (ch < 'A' || ch > 'Z')
            {
                return false;
            }
        }
        return true;
    }
}
