using System.Text.Json;

namespace EventSourcingCqrs.Hosts.Api;

// The transport envelope for POST /commands (D3). The type discriminator names
// the command; the payload carries its fields. Payload stays a JsonElement so the
// endpoint can defer its deserialization until the discriminator resolves to a
// concrete command type through CommandTypeRegistry (ADR 0023).
public sealed record CommandEnvelope(string Type, JsonElement Payload);
