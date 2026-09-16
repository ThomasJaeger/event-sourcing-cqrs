namespace EventSourcingCqrs.Domain.Abstractions;

public interface IOrderWorkflowCommand : ICommand
{
    Guid OrderId { get; }
}
