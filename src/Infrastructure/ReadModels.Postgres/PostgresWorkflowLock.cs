using System.Security.Cryptography;
using System.Text;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

// All event-store providers share the read-model PostgreSQL database. Transaction-scoped
// advisory locks coordinate API commands, event handlers, and timeout workers;
// they contain no business state and disappear when the connection is lost.
public sealed class PostgresWorkflowLock(IReadModelConnectionFactory factory) : IWorkflowLock
{
    private readonly AsyncLocal<string?> _owner = new();

    public async Task RunAsync(TenantId tenant, Guid orderId, Func<Task> action, CancellationToken ct)
    {
        var identity = $"order-workflow:{tenant.Value:N}:{orderId:N}";
        if (_owner.Value == identity)
        {
            await action();
            return;
        }
        var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        await using var connection = await factory.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@key)";
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(ct);
        var previous = _owner.Value;
        _owner.Value = identity;
        try { await action(); }
        finally { _owner.Value = previous; }
        // Disposing the transaction releases the lock, including on exceptions.
    }
}
