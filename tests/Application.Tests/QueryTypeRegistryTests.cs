using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests;

public class QueryTypeRegistryTests
{
    [Fact]
    public void Register_succeeds_when_type_is_new()
    {
        var registry = new QueryTypeRegistry().Register(typeof(QueryA));

        registry.NameFor(typeof(QueryA)).Should().Be(nameof(QueryA));
        registry.TypeFor(nameof(QueryA)).Should().Be(typeof(QueryA));
    }

    [Fact]
    public void Register_returns_self_for_fluent_chaining()
    {
        var registry = new QueryTypeRegistry()
            .Register(typeof(QueryA))
            .Register(typeof(QueryB));

        registry.NameFor(typeof(QueryA)).Should().Be(nameof(QueryA));
        registry.NameFor(typeof(QueryB)).Should().Be(nameof(QueryB));
    }

    [Fact]
    public void Register_with_explicit_name_uses_that_name()
    {
        var registry = new QueryTypeRegistry().Register(typeof(QueryA), "query_a_v1");

        registry.NameFor(typeof(QueryA)).Should().Be("query_a_v1");
        registry.TypeFor("query_a_v1").Should().Be(typeof(QueryA));
    }

    [Fact]
    public void Register_throws_when_same_clr_type_registered_twice()
    {
        var registry = new QueryTypeRegistry().Register(typeof(QueryA));

        var act = () => registry.Register(typeof(QueryA), "alias");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*QueryA*already registered*alias*");
    }

    [Fact]
    public void Register_throws_when_same_storage_name_registered_twice()
    {
        var registry = new QueryTypeRegistry().Register(typeof(QueryA), "collision");

        var act = () => registry.Register(typeof(QueryB), "collision");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'collision'*already registered*QueryA*QueryB*");
    }

    [Fact]
    public void NameFor_throws_UnknownQueryTypeException_when_type_not_registered()
    {
        var registry = new QueryTypeRegistry();

        var act = () => registry.NameFor(typeof(QueryA));

        act.Should().Throw<UnknownQueryTypeException>()
            .Which.TypeName.Should().Contain(nameof(QueryA));
    }

    [Fact]
    public void TypeFor_throws_UnknownQueryTypeException_when_name_not_registered()
    {
        var registry = new QueryTypeRegistry();

        var act = () => registry.TypeFor("not_a_real_query");

        act.Should().Throw<UnknownQueryTypeException>()
            .Which.TypeName.Should().Be("not_a_real_query");
    }

    [Fact]
    public void TypeFor_is_case_sensitive()
    {
        var registry = new QueryTypeRegistry().Register(typeof(QueryA), "QueryA");

        var act = () => registry.TypeFor("querya");

        act.Should().Throw<UnknownQueryTypeException>();
    }

    [Fact]
    public void Register_throws_when_Type_does_not_implement_IQuery()
    {
        var registry = new QueryTypeRegistry();

        // The event-store registries reject a non-family type through an
        // IsAssignableFrom check on a non-generic marker. IQuery<TResult> has no
        // non-generic marker, so the guard is a reflection check that the type
        // closes IQuery<> for some TResult (ADR 0022).
        var act = () => registry.Register(typeof(NotAQuery));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*NotAQuery*does not implement IQuery*");
    }

    [Fact]
    public void EnumerateQueries_returns_all_registered_types()
    {
        var registry = new QueryTypeRegistry()
            .Register(typeof(QueryA))
            .Register(typeof(QueryB));

        registry.EnumerateQueries().Should().BeEquivalentTo(
            new[] { typeof(QueryA), typeof(QueryB) });
    }

    private sealed record QueryA : IQuery<string>;
    private sealed record QueryB : IQuery<int>;
    private sealed class NotAQuery;
}
