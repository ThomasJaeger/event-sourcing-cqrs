using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;

namespace EventSourcingCqrs.IntegrationTests.AdminConsole;

internal static class AdminConsoleTestConfiguration
{
    private static readonly string PasswordHash =
        new PasswordHasher<string>().HashPassword("operator", "An admin fixture-only password!");

    public static IWebHostBuilder WithOperatorCredentials(this IWebHostBuilder builder) => builder
        .UseSetting("BootstrapAdministrator:AdministratorUserId", AdminTestAuthHandler.AdminActorId.ToString())
        .UseSetting("OperatorAuthentication:PasswordHash", PasswordHash);
}
