using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;

namespace EventSourcingCqrs.Hosts.Authentication;

// The single configured operator proves possession of a password before receiving a cookie.
// Configuration holds a salted Identity password hash, never a plaintext credential.
internal sealed class OperatorPassword
{
    public const string ConfigurationKey = "OperatorAuthentication:PasswordHash";
    // A new protection purpose invalidates cookies issued by the former passwordless login.
    public const string AuthenticationScheme = "OperatorPasswordV1";
    private readonly PasswordHasher<OperatorPassword> _hasher = new();
    private readonly string _hash;

    public OperatorPassword(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException($"{ConfigurationKey} is not set.");
        try
        {
            var bytes = Convert.FromBase64String(hash);
            if (bytes.Length < 61 || bytes[0] != 1)
                throw new FormatException();
            var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
            var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
            var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4));
            if (prf > 2 || iterations is 0 or > int.MaxValue
                || saltLength < 16 || saltLength > bytes.Length - 13 - 16)
                throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{ConfigurationKey} must contain an Identity V3 password hash.");
        }
        _hash = hash;
    }

    public bool Verify(string? password)
        => !string.IsNullOrEmpty(password) && password.Length <= 1024
            && _hasher.VerifyHashedPassword(this, _hash, password) != PasswordVerificationResult.Failed;
}
