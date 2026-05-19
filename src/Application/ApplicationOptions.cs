namespace EventSourcingCqrs.Application;

// Host-configurable knobs that the command pipeline reads. ServiceName ends up
// on every event's metadata.Source so a stream's events tell you which host
// emitted them; hosts override the default via AddApplication's configure
// callback. Mutable property rather than init-only so the AddApplication
// extension can apply the host's configure delegate to a single instance
// before registering it.
public sealed class ApplicationOptions
{
    public string ServiceName { get; set; } = "Workers";
}
