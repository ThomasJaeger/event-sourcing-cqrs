using EventSourcingCqrs.Application.Authorization;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authorization;

public class QueryPermissionValidationTests
{
    // A concrete query that declares no permission. It lives in the test assembly, so the real
    // composition walk never sees it; it proves FindUndeclared catches the gap without planting a
    // non-conforming query in the Application assembly the structural check scans.
    private sealed record UndeclaredQuery : IQuery<string>;

    [Fact]
    public void Every_concrete_query_in_the_application_assembly_declares_a_permission()
    {
        var undeclared = QueryPermissionValidation.FindUndeclared(
            typeof(ListOrders).Assembly.GetTypes());

        undeclared.Should().BeEmpty();
    }

    [Fact]
    public void FindUndeclared_flags_a_query_that_declares_no_permission()
    {
        var undeclared = QueryPermissionValidation.FindUndeclared(new[] { typeof(UndeclaredQuery) });

        undeclared.Should().ContainSingle().Which.Should().Be(typeof(UndeclaredQuery));
    }

    [Fact]
    public void FindUndeclared_ignores_a_query_that_declares_a_permission()
    {
        var undeclared = QueryPermissionValidation.FindUndeclared(new[] { typeof(ListOrders) });

        undeclared.Should().BeEmpty();
    }
}
