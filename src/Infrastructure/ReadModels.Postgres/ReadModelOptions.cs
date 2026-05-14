namespace EventSourcingCqrs.Infrastructure.ReadModels.Postgres;

public sealed class ReadModelOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}
