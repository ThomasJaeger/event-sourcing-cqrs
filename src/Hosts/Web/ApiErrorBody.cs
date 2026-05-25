namespace EventSourcingCqrs.Hosts.Web;

/// <summary>
/// Wire-format error body the Api host's ExceptionMappingMiddleware emits.
/// Two shapes share this DTO: validation responses populate Errors (a
/// path-to-messages map from HttpValidationProblemDetails); other failures
/// populate Code and Message (the anonymous JSON shape the middleware emits
/// for 422, 409, 404, and 500). ExpectedVersion populates on concurrency
/// failures (409); other arms leave it null. This is the Web's parsing
/// contract; the Api emits anonymous JSON, so a future refactor unifying
/// the wire format would touch both ends.
/// </summary>
internal sealed record ApiErrorBody(
    string? Code,
    string? Message,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors,
    int? ExpectedVersion);
