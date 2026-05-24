using System.Text.Json;

namespace EventSourcingCqrs.Hosts.Api;

// The transport envelope for POST /queries, parallel to CommandEnvelope. The type
// discriminator names the query; the payload carries its fields. Payload stays a
// JsonElement so the endpoint defers its deserialization until the discriminator
// resolves to a concrete query type through QueryTypeRegistry (ADR 0022).
public sealed record QueryEnvelope(string Type, JsonElement Payload);
