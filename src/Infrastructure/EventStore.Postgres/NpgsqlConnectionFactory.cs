using Npgsql;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public sealed class NpgsqlConnectionFactory : INpgsqlConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
        => await _dataSource.OpenConnectionAsync(ct);
}
