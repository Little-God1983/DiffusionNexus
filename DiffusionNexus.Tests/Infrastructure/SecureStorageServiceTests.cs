using System.Text;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Infrastructure;

/// <summary>
/// Guards the at-rest protection of the user's Civitai / HuggingFace API keys.
/// <para>
/// <see cref="SecureStorageService"/> branches on the OS (DPAPI on Windows,
/// AES + PBKDF2 elsewhere), so every assertion here is deliberately written to
/// hold on <b>both</b> code paths: round-trip fidelity, the empty/null contract,
/// and the fact that both methods swallow every exception and return
/// <c>null</c> rather than propagating a <see cref="System.Security.Cryptography.CryptographicException"/>.
/// </para>
/// </summary>
public class SecureStorageServiceTests
{
    private static ISecureStorage CreateSut() => new SecureStorageService();

    // ── Round-trip fidelity ──────────────────────────────────────────────

    [Fact]
    public void WhenValueIsEncryptedThenDecryptingReturnsTheOriginalValue()
    {
        var sut = CreateSut();
        const string apiKey = "civitai_9f2c4a1b7e8d0364a5b1c9e7f2d8a6b4";

        var cipher = sut.Encrypt(apiKey);
        var roundTripped = sut.Decrypt(cipher);

        cipher.Should().NotBeNullOrEmpty();
        roundTripped.Should().Be(apiKey);
    }

    [Fact]
    public void WhenValueContainsUnicodeThenRoundTripPreservesEveryCharacter()
    {
        var sut = CreateSut();
        // Multi-byte UTF-8, combining marks, CJK and an astral-plane emoji:
        // a naive byte/char length assumption in the crypto layer would corrupt these.
        const string value = "café · naïve · 日本語のトークン · Ключ · 🔑🗝️";

        var roundTripped = sut.Decrypt(sut.Encrypt(value));

        roundTripped.Should().Be(value);
    }

    [Fact]
    public void WhenValueIsVeryLongThenRoundTripPreservesItExactly()
    {
        var sut = CreateSut();
        // Well past any single AES block / stream-buffer boundary.
        var value = string.Concat(Enumerable.Range(0, 2000).Select(i => $"segment-{i};"));

        var roundTripped = sut.Decrypt(sut.Encrypt(value));

        roundTripped.Should().Be(value);
        roundTripped!.Length.Should().Be(value.Length);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   \t  ")]
    [InlineData("\n")]
    public void WhenValueIsWhitespaceOnlyThenItIsStillEncryptedAndPreserved(string value)
    {
        // The guard clause is IsNullOrEmpty, NOT IsNullOrWhitespace — whitespace
        // is real data and must survive the round trip untouched.
        var sut = CreateSut();

        var cipher = sut.Encrypt(value);

        cipher.Should().NotBeNull();
        sut.Decrypt(cipher).Should().Be(value);
    }

    [Fact]
    public void WhenValueHasSurroundingWhitespaceThenRoundTripDoesNotTrimIt()
    {
        var sut = CreateSut();
        const string value = "  key-with-padding  ";

        sut.Decrypt(sut.Encrypt(value)).Should().Be(value);
    }

    [Fact]
    public void WhenEncryptedByOneInstanceThenAnotherInstanceCanDecryptIt()
    {
        // The service is stateless — no key material is held per instance,
        // so a value written by the settings VM must be readable by the
        // download service resolved from a different DI scope.
        var writer = CreateSut();
        var reader = CreateSut();
        const string value = "hf_AbCdEfGhIjKlMnOpQrStUvWxYz012345";

        reader.Decrypt(writer.Encrypt(value)).Should().Be(value);
    }

    // ── Null / empty contract ────────────────────────────────────────────

    [Fact]
    public void WhenEncryptingNullThenNullIsReturned()
    {
        CreateSut().Encrypt(null).Should().BeNull();
    }

    [Fact]
    public void WhenEncryptingEmptyStringThenNullIsReturned()
    {
        // Documented contract: empty in => null out (never an empty cipher text).
        CreateSut().Encrypt(string.Empty).Should().BeNull();
    }

    [Fact]
    public void WhenDecryptingNullThenNullIsReturned()
    {
        CreateSut().Decrypt(null).Should().BeNull();
    }

    [Fact]
    public void WhenDecryptingEmptyStringThenNullIsReturned()
    {
        CreateSut().Decrypt(string.Empty).Should().BeNull();
    }

    // ── Failure handling: swallow, never throw ───────────────────────────

    [Theory]
    [InlineData("not base64 at all !!!")]
    [InlineData("****")]
    [InlineData("a")]           // invalid Base64 length
    [InlineData("=====")]
    public void WhenDecryptingNonBase64InputThenNullIsReturnedInsteadOfThrowing(string garbage)
    {
        var sut = CreateSut();

        string? result = null;
        var act = () => result = sut.Decrypt(garbage);

        act.Should().NotThrow("Decrypt swallows all exceptions by design");
        result.Should().BeNull();
    }

    [Fact]
    public void WhenDecryptingValidBase64ThatIsNotCipherTextThenNullIsReturned()
    {
        // "Hello World" as Base64 — decodes fine, but is far too short to be a
        // DPAPI blob or a salt(32) + IV(16) AES payload. Both platforms must
        // report failure as null rather than surfacing a CryptographicException
        // (or an IndexOutOfRange) to the settings UI.
        var sut = CreateSut();
        var notCipherText = Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello World"));

        string? result = null;
        var act = () => result = sut.Decrypt(notCipherText);

        act.Should().NotThrow();
        result.Should().BeNull();
    }

    [Fact]
    public void WhenDecryptingEmptyBase64PayloadThenNullIsReturned()
    {
        var sut = CreateSut();

        // "AAAA" decodes to 3 zero bytes.
        sut.Decrypt("AAAA").Should().BeNull();
    }

    [Fact]
    public void WhenCipherTextIsCorruptedThenTheOriginalValueIsNotRecovered()
    {
        var sut = CreateSut();
        const string secret = "civitai-token-do-not-leak";

        var bytes = Convert.FromBase64String(sut.Encrypt(secret)!);
        // Flip bits inside the payload (past any header) to simulate a truncated
        // or hand-edited settings row.
        for (var i = bytes.Length / 2; i < bytes.Length; i++)
        {
            bytes[i] ^= 0xFF;
        }

        string? result = null;
        var act = () => result = sut.Decrypt(Convert.ToBase64String(bytes));

        act.Should().NotThrow("corruption must degrade to a null read, not crash settings load");
        result.Should().NotBe(secret);
    }

    [Fact]
    public void WhenCipherTextIsTruncatedThenNullIsReturned()
    {
        var sut = CreateSut();

        var bytes = Convert.FromBase64String(sut.Encrypt("some-api-key")!);
        // Keep only the first few bytes — shorter than any valid header.
        var truncated = bytes.Take(4).ToArray();

        sut.Decrypt(Convert.ToBase64String(truncated)).Should().BeNull();
    }

    // ── The cipher text itself ───────────────────────────────────────────

    [Fact]
    public void WhenValueIsEncryptedThenTheResultIsValidBase64AndNotThePlainText()
    {
        var sut = CreateSut();
        const string secret = "PLAINTEXT-MARKER-12345";

        var cipher = sut.Encrypt(secret);

        cipher.Should().NotBeNull().And.NotBe(secret);
        var decode = () => Convert.FromBase64String(cipher!);
        decode.Should().NotThrow("the contract promises a Base64-encoded string");
    }

    [Fact]
    public void WhenValueIsEncryptedThenThePlainTextDoesNotAppearInTheCipherBytes()
    {
        var sut = CreateSut();
        const string secret = "PLAINTEXT-MARKER-12345";

        var cipher = sut.Encrypt(secret)!;
        // Latin1 maps bytes 1:1 to chars, so an ASCII secret would show up verbatim
        // if the payload were merely encoded rather than encrypted.
        var raw = Encoding.Latin1.GetString(Convert.FromBase64String(cipher));

        cipher.Should().NotContain(secret);
        raw.Should().NotContain(secret);
    }

    [Fact]
    public void WhenTheSameValueIsEncryptedTwiceThenTheCipherTextsDiffer()
    {
        // Both back-ends inject fresh randomness (DPAPI blob entropy; a random
        // salt + IV for AES), so an attacker cannot fingerprint a known key by
        // comparing stored cipher texts across machines or saves.
        var sut = CreateSut();
        const string value = "identical-input";

        var first = sut.Encrypt(value);
        var second = sut.Encrypt(value);

        first.Should().NotBe(second);
        sut.Decrypt(first).Should().Be(value);
        sut.Decrypt(second).Should().Be(value);
    }

    [Fact]
    public void WhenDifferentValuesAreEncryptedThenEachDecryptsBackToItsOwnValue()
    {
        var sut = CreateSut();

        var civitai = sut.Encrypt("civitai-key");
        var huggingface = sut.Encrypt("huggingface-key");

        sut.Decrypt(civitai).Should().Be("civitai-key");
        sut.Decrypt(huggingface).Should().Be("huggingface-key");
    }
}
