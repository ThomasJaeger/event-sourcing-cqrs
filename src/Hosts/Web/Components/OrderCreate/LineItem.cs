using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Hosts.Web.Components.OrderCreate;

// The wizard's per-line working state, accumulated across the order-create flow
// and read by the line-items and review steps. LineId is minted client-side
// per line (the AddOrderLine command carries it). Commit 25 wires the add and
// remove dispatches that populate this list; for now it stays empty. Extracted
// to its own file rather than nested in OrderCreate because the step components
// live in the OrderCreate namespace, whose segment would shadow the page class
// if they referenced a nested OrderCreate.LineItem.
public sealed record LineItem(Guid LineId, string Sku, int Quantity, Money UnitPrice);
