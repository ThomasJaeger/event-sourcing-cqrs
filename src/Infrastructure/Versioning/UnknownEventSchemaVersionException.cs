namespace EventSourcingCqrs.Infrastructure.Versioning;

// Raised when a stored schema version has no place in a registered event lineage: a version
// below 1, or above the lineage's current version, which the upcaster pipeline cannot resolve
// to a type or lift to the current shape. Parallel to UnknownEventTypeException, which reports
// an unregistered storage name; this reports a known name at an unknown version. Pattern from
// Chapter 11: Upcasting.
public sealed class UnknownEventSchemaVersionException : Exception
{
    public string StorageName { get; }
    public int Version { get; }

    public UnknownEventSchemaVersionException(string storageName, int version)
        : base($"No schema version {version} is registered for stored event type '{storageName}'.")
    {
        StorageName = storageName;
        Version = version;
    }
}
