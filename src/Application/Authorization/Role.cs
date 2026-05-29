namespace EventSourcingCqrs.Application.Authorization;

// A named role a principal can hold. A principal can hold several. The System role holds the
// permission set process managers exercise; the async-propagation commit wires the system actor
// to it. It is defined here so the role-to-permission policy validates as a complete set.
public enum Role
{
    Customer,
    Support,
    Admin,
    System,
}
