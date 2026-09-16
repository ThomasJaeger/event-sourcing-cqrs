namespace EventSourcingCqrs.Domain.Abstractions;

// Internal control flow: the authoritative stream already contains this operation.
public sealed class CommandAlreadyCommittedException : Exception
{
    public CommandAlreadyCommittedException() : base("The command has already committed.") { }
}
