using Microsoft.Data.SqlClient;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Microsoft.Data.SqlClient pools connections behind the connection string, so there is no
// NpgsqlDataSource counterpart to hold. The factory owns the string and opens from the pool.
public sealed class SqlServerConnectionFactory : ISqlServerConnectionFactory
{
    private readonly string _connectionString;

    public SqlServerConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
    }

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
