using Npgsql;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

public sealed class NpgsqlReadModelConnectionFactory
    : IReadModelConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlReadModelConnectionFactory(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
        => await _dataSource.OpenConnectionAsync(ct);

    // AddReadModels builds the NpgsqlDataSource inside this factory rather than
    // registering it as a bare container singleton (see Session 0005's commit-6
    // decision in ServiceCollectionExtensions.cs). The container therefore has
    // no separate handle on the data source; disposing this factory at host
    // shutdown is what releases the underlying connection pool. The Microsoft
    // DI container checks the runtime type of a singleton instance, not its
    // registration type, when deciding what to dispose, so IAsyncDisposable on
    // the concrete is enough; AddReadModels does not need to change.
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
