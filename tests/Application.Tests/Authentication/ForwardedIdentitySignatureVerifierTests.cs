using System.Buffers.Text;
using EventSourcingCqrs.Application.Authentication;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Authentication;

// Direct coverage for the signature verifier's accept and reject branches, now that it is a shared
// Application component both hosts depend on. The acceptance path is also exercised cross-host by the
// integration signer-acceptance test; these pin the reject branches as units.
public class ForwardedIdentitySignatureVerifierTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef"; // exactly 32 characters

    private static ForwardedIdentitySigningKey Key() =>
        new(new ForwardedIdentitySigningOptions { Secret = Secret });

    [Fact]
    public void Verify_accepts_a_value_signed_with_the_shared_key()
    {
        var key = Key();
        var verifier = new ForwardedIdentitySignatureVerifier(key);
        var value = $"{Guid.NewGuid():N};";
        var signature = Base64Url.EncodeToString(key.ComputeHash(value));

        verifier.Verify(value, signature).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_rejects_a_missing_signature(string? signature)
    {
        var verifier = new ForwardedIdentitySignatureVerifier(Key());

        verifier.Verify($"{Guid.NewGuid():N};", signature).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_a_non_base64url_signature()
    {
        var verifier = new ForwardedIdentitySignatureVerifier(Key());

        // '@' is outside the base64url alphabet, so the decode throws and Verify returns false.
        verifier.Verify($"{Guid.NewGuid():N};", "@@@not-base64url@@@").Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_a_well_formed_signature_of_a_different_value()
    {
        var key = Key();
        var verifier = new ForwardedIdentitySignatureVerifier(key);
        var value = $"{Guid.NewGuid():N};";
        // A valid base64url MAC computed over a different value, so the constant-time compare fails.
        var wrongSignature = Base64Url.EncodeToString(key.ComputeHash(value + "tampered"));

        verifier.Verify(value, wrongSignature).Should().BeFalse();
    }
}
