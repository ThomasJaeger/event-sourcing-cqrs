namespace EventSourcingCqrs.Application.Queries.Sales;

public sealed class OrderHistoryUnavailableException() : Exception(
    "Order history is not available on this deployment's event store.");

public sealed class OrderHistoryTooLongException() : Exception(
    "This order has more than 500 events. Use the event tools to inspect its history.");

public sealed class OrderHistoryIncompleteException(Exception? innerException = null) : Exception(
    "The recorded order history could not be reconstructed. Use the event tools to inspect it.", innerException);
