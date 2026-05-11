using EventStore.Postgres;

const string usage = "Usage: EventStore.Postgres.Cli migrate [--dry-run]";

if (args.Length == 0 || args[0] != "migrate")
{
    Console.Error.WriteLine(usage);
    return 64; // EX_USAGE
}

var dryRun = false;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--dry-run")
    {
        dryRun = true;
    }
    else
    {
        Console.Error.WriteLine(usage);
        return 64;
    }
}

var connectionString = Environment.GetEnvironmentVariable("EVENT_STORE_CONNECTION_STRING");
if (string.IsNullOrEmpty(connectionString))
{
    Console.Error.WriteLine("EVENT_STORE_CONNECTION_STRING is not set.");
    return 78; // EX_CONFIG
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    var runner = new MigrationRunner();
    await runner.RunPendingAsync(
        new MigrationRunnerOptions
        {
            ConnectionString = connectionString,
            DryRun = dryRun,
            Log = Console.WriteLine,
        },
        cts.Token);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
