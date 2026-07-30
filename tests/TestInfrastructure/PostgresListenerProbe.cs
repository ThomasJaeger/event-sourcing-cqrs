using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.TestInfrastructure;

// Observes PostgreSQL LISTEN sessions through pg_stat_activity, so a test can wait for a
// listener to attach instead of sleeping for a duration and hoping.
//
// Why this exists: pg_notify does not queue for absent listeners. A test that publishes
// before the listener has issued its LISTEN loses the notification permanently, and the
// failure surfaces far downstream as a missing log entry or an unfired wake rather than as
// "the listener was not attached yet". A CI run in Phase 17 lost exactly that way: a 500 ms
// sleep was long enough on a developer machine and short enough under CI load.
//
// How the observation works: pg_stat_activity.query records a backend's most recent query.
// A listener parked in NpgsqlConnection.WaitAsync has LISTEN as its most recent query and
// sits in state 'idle', so the pair identifies it. Both adapters quote the channel name
// into the command text (OutboxProcessor and PostgresHubBackplaneConnection both build
// LISTEN "channel"), so matching the channel inside the query text is what keeps this from
// being satisfied by some unrelated idle connection.
//
// The probe is deliberately database-scoped through datname = current_database(). Every
// test here takes its own database from PostgresFixture, so a probe that ignored datname
// could observe, and terminate, a listener belonging to a different test.
public static class PostgresListenerProbe
{
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    // Short enough that the fast path costs a fraction of the fixed sleeps this replaces.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    // Waits until at least one backend is listening on the channel and returns their pids.
    // Callers needing a count read it off the returned list.
    //
    // excluding names pids that must not satisfy the wait. That is what makes a reconnect
    // observable: pg_terminate_backend kills a session by pid and the replacement gets a new
    // one, so waiting for a listener whose pid is not the terminated one is the difference
    // between observing the reconnect and observing the session that never went away.
    public static async Task<IReadOnlyList<int>> WaitForListenersAsync(
        string connectionString,
        string channelName,
        TimeSpan? budget = null,
        IReadOnlyCollection<int>? excluding = null,
        CancellationToken ct = default)
    {
        var window = budget ?? DefaultBudget;
        var deadline = DateTime.UtcNow + window;

        while (true)
        {
            var pids = await ListeningPidsAsync(connectionString, channelName, excluding, ct);
            if (pids.Count > 0)
            {
                return pids;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(PollInterval, ct);
        }

        var excluded = excluding is { Count: > 0 }
            ? $", excluding pid(s) {string.Join(", ", excluding)}"
            : string.Empty;
        throw new TimeoutException(
            $"No backend listening on channel '{channelName}' became visible in " +
            $"pg_stat_activity within {window}{excluded}. The listener never attached, or it " +
            $"attached on a different database.");
    }

    // The single observation the wait polls. Public because a test that wants the current
    // state without waiting (an assertion that a listener is gone, say) needs the same query.
    public static async Task<IReadOnlyList<int>> ListeningPidsAsync(
        string connectionString,
        string channelName,
        IReadOnlyCollection<int>? excluding = null,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT pid FROM pg_stat_activity " +
            "WHERE pid <> pg_backend_pid() " +
            "  AND datname = current_database() " +
            "  AND state = 'idle' " +
            "  AND query LIKE '%LISTEN%' " +
            "  AND query LIKE '%' || @channel || '%' " +
            "  AND NOT (pid = ANY(@excluding))";
        cmd.Parameters.AddWithValue("channel", NpgsqlDbType.Text, channelName);
        cmd.Parameters.AddWithValue(
            "excluding",
            NpgsqlDbType.Array | NpgsqlDbType.Integer,
            (excluding ?? []).ToArray());

        var pids = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pids.Add(reader.GetInt32(0));
        }

        return pids;
    }

    // Terminates exactly the named backends and returns how many died.
    // Terminating by observed pid rather than by a re-run of the matching predicate is what
    // lets a caller assert the count: the number asked for and the number killed are
    // comparable, so a terminate that hit nothing reports as a failed assertion instead of
    // passing vacuously while the listener survives.
    public static async Task<int> TerminateAsync(
        string connectionString,
        IReadOnlyCollection<int> pids,
        CancellationToken ct = default)
    {
        if (pids.Count == 0)
        {
            return 0;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM (" +
            "  SELECT pg_terminate_backend(pid) AS killed FROM unnest(@pids) AS t(pid)" +
            ") s WHERE s.killed";
        cmd.Parameters.AddWithValue(
            "pids", NpgsqlDbType.Array | NpgsqlDbType.Integer, pids.ToArray());
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
