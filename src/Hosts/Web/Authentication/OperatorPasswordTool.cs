using System.Text;
using Microsoft.AspNetCore.Identity;

namespace EventSourcingCqrs.Hosts.Web.Authentication;

// Generate a deployment credential without placing the password in command-line arguments.
internal static class OperatorPasswordTool
{
    public static void Run()
    {
        Console.Error.Write("Operator password (16 to 1024 characters): ");
        var password = ReadPassword();
        if (password.Length is < 16 or > 1024)
        {
            Console.Error.WriteLine("Password must contain 16 to 1024 characters.");
            Environment.ExitCode = 2;
            return;
        }
        Console.WriteLine(new PasswordHasher<string>().HashPassword("operator", password));
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return value.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && value.Length > 0)
                value.Length--;
            else if (!char.IsControl(key.KeyChar))
                value.Append(key.KeyChar);
        }
    }
}
