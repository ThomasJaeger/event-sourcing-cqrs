namespace EventSourcingCqrs.Domain.SharedKernel;

public sealed record Address(
    string Street,
    string City,
    string PostalCode,
    string Country);
