using System.Security.Cryptography;
using IntegracaoSolidesDP.Worker.Management;

namespace IntegracaoSolidesDP.Tests.Management;

public sealed class TokenProtectorTests
{
    private static TokenProtector WithKey(string? key) => new(new TokenProtectionOptions { ChaveBase64 = key });

    [Fact]
    public void Configured_key_round_trips_and_never_stores_the_plain_token()
    {
        var protector = WithKey(TokenProtector.NovaChave());

        var blob = protector.Protect("  meu-token-secreto ");

        blob[0].Should().Be(2, "versão 2 = AES-256-GCM");
        System.Text.Encoding.UTF8.GetString(blob).Should().NotContain("meu-token-secreto");
        protector.Unprotect(blob).Should().Be("meu-token-secreto");
    }

    [Fact]
    public void Each_protection_uses_a_new_nonce()
    {
        var protector = WithKey(TokenProtector.NovaChave());

        protector.Protect("abc").Should().NotEqual(protector.Protect("abc"));
    }

    [Fact]
    public void Another_key_cannot_read_the_token()
    {
        var blob = WithKey(TokenProtector.NovaChave()).Protect("abc");

        var read = () => WithKey(TokenProtector.NovaChave()).Unprotect(blob);

        read.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Tampered_data_is_refused()
    {
        var protector = WithKey(TokenProtector.NovaChave());
        var blob = protector.Protect("abc");
        blob[^1] ^= 0xFF;

        var read = () => protector.Unprotect(blob);

        read.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Key_with_the_wrong_size_is_refused()
    {
        var protect = () => WithKey(Convert.ToBase64String(new byte[16])).Protect("abc");

        protect.Should().Throw<CryptographicException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void Token_encrypted_with_the_key_needs_the_key_to_be_read()
    {
        var blob = WithKey(TokenProtector.NovaChave()).Protect("abc");

        var read = () => WithKey(null).Unprotect(blob);

        read.Should().Throw<CryptographicException>().WithMessage("*chave*");
    }
}
