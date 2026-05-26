namespace EventSourcingCqrs.Hosts.Web;

/// <summary>
/// Base type for all failure responses surfaced by ApiClient. Consumers
/// catch a specific subtype to handle a specific failure category, or
/// catch ApiClientException to handle any Api-side failure uniformly.
/// The four subtypes map to the Api host's middleware arms (400, 422,
/// 409) plus a catch-all for 500, 404 (the rare race where the aggregate
/// vanished between query and dispatch), and any unrecognized response.
/// </summary>
public abstract class ApiClientException : Exception
{
    protected ApiClientException(string message) : base(message) { }
}

public sealed class ApiValidationException : ApiClientException
{
    // Errors carries the field-to-messages map from a dispatch-time validation
    // failure (the Api host's HttpValidationProblemDetails 400). Code and Message
    // carry the structured body of a pre-dispatch endpoint 400 (a missing header,
    // an unknown type discriminator, a malformed payload), which has no Errors map.
    // One arm of the two is populated per 400; the other stays empty.
    public ApiValidationException(
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors,
        string? code = null,
        string? message = null)
        : base(message ?? "The request was invalid.")
    {
        Errors = errors;
        Code = code;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; }

    public string? Code { get; }
}

public sealed class ApiBusinessRuleException : ApiClientException
{
    public string Code { get; }

    public ApiBusinessRuleException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

public sealed class ApiConcurrencyException : ApiClientException
{
    public int ExpectedVersion { get; }

    public ApiConcurrencyException(int expectedVersion)
        : base("The command conflicted with a concurrent update.")
    {
        ExpectedVersion = expectedVersion;
    }
}

public sealed class ApiInfrastructureException : ApiClientException
{
    public int? StatusCode { get; }
    public string? Code { get; }

    public ApiInfrastructureException(string message, int? statusCode = null, string? code = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}
