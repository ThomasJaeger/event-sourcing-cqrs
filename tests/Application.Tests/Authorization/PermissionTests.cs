using EventSourcingCqrs.Application.Authorization;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authorization;

public class PermissionTests
{
    [Fact]
    public void An_unknown_permission_name_is_not_a_defined_permission()
    {
        Enum.TryParse<Permission>("NotARealPermission", out _).Should().BeFalse();
    }

    [Fact]
    public void An_out_of_range_permission_value_is_not_defined()
    {
        Enum.IsDefined((Permission)9999).Should().BeFalse();
    }
}
