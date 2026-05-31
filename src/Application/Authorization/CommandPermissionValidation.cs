using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

// The composition-time structural check: every concrete command in the Application assembly must
// declare a required permission by implementing IAuthorizedCommand. Factored from the registration
// site so the failure path is provable in a unit test without planting a non-conforming command in
// the assembly the real walk scans. The walk runs against the Application assembly, so the two
// ProcessManagers-assembly timeout commands fall outside it by assembly boundary and declare their
// permission at the caused-command commit.
internal static class CommandPermissionValidation
{
    public static IReadOnlyCollection<Type> FindUndeclared(IEnumerable<Type> candidateTypes)
        => candidateTypes
            .Where(t => t is { IsAbstract: false } && typeof(ICommand).IsAssignableFrom(t))
            .Where(t => !typeof(IAuthorizedCommand).IsAssignableFrom(t))
            .ToArray();
}
