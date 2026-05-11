namespace EventStore.Postgres;

public sealed class MigrationChecksumMismatchException : Exception
{
    public MigrationChecksumMismatchException(int version, string name, string stored, string computed)
        : base(
            $"Migration {version:0000}_{name} checksum mismatch. Stored: {stored}. Computed: {computed}. " +
            "The migration file was edited after being applied. " +
            "Revert the file to its original contents or write a new migration that supersedes it.")
    {
        Version = version;
        Name = name;
        Stored = stored;
        Computed = computed;
    }

    public int Version { get; }

    public string Name { get; }

    public string Stored { get; }

    public string Computed { get; }
}
