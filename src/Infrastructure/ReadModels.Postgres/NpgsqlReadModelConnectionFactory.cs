using Npgsql;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

public sealed class NpgsqlReadModelConnectionFactory : IReadModelConnectionFactory
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlReadModelConnectionFactory(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
        => await _dataSource.OpenConnectionAsync(ct);
}
