using FluentAssertions;
using Xunit;

namespace RadiologyPlus.NovaradAuth.Tests;

/// <summary>
/// Goldens for <see cref="NovaradPasswordHasher"/>. Vectors were computed externally
/// (Python + PyCryptodome) so this suite does not self-verify — if our algorithm
/// drifts from the spec in <c>docs/novarad-password-algorithm.md</c>, these tests
/// fail.
///
/// Live-DB shape evidence (Novarad clone @ 192.168.0.200/novarad, sampled while
/// writing this suite):
///   password_format=0 row: password column length = 8 (matches plaintext)
///   password_format=1 row: password column length = 95 (= 32 bytes × 3 − 1, the
///                          length of BitConverter.ToString(SHA-256 of 32 bytes))
/// </summary>
public sealed class NovaradPasswordHasherTests
{
    private readonly NovaradPasswordHasher _hasher = new();

    // -----------------------------------------------------------------------
    // Format 0 — plaintext (citext compare)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("password")]
    [InlineData("Novarad!23")]
    public void Format0_Encode_returns_the_password_as_is(string plaintext)
    {
        _hasher.Encode(plaintext, passwordFormat: 0, salt: null, systemEncryptionKey: null)
            .Should().Be(plaintext);
    }

    [Theory]
    [InlineData("password", "password")]
    [InlineData("password", "PASSWORD")]  // citext is case-insensitive
    [InlineData("Novarad!23", "novarad!23")]
    public void Format0_Verify_is_case_insensitive(string typed, string stored)
    {
        _hasher.Verify(typed, stored, passwordFormat: 0, salt: null, systemEncryptionKey: null)
            .Should().BeTrue();
    }

    [Fact]
    public void Format0_Verify_rejects_wrong_password()
    {
        _hasher.Verify("right", "wrong", passwordFormat: 0, salt: null, systemEncryptionKey: null)
            .Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Format 1 — unsalted SHA-256 of UTF-16LE password
    //
    // Golden values from:
    //   python -c "import hashlib; print(hashlib.sha256(s.encode('utf-16-le')).hexdigest())"
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("",            "E3-B0-C4-42-98-FC-1C-14-9A-FB-F4-C8-99-6F-B9-24-27-AE-41-E4-64-9B-93-4C-A4-95-99-1B-78-52-B8-55")]
    [InlineData("a",           "FF-E9-AA-EA-A2-A2-D5-04-81-74-DF-0B-80-59-9E-F0-19-7E-C0-24-C4-B0-51-BC-98-60-CF-F5-8E-F7-F9-F3")]
    [InlineData("password",    "E2-01-06-5D-05-54-65-26-15-C3-20-C0-0A-1D-5B-C8-ED-CA-46-9D-72-C2-79-0E-24-15-2D-0C-1E-2B-61-89")]
    [InlineData("Novarad!23",  "D4-ED-EF-11-60-CD-44-6A-11-34-04-42-28-BE-5F-E1-AD-4A-A5-51-19-07-D3-F3-4C-2E-FA-56-98-10-A2-8F")]
    [InlineData("eclair",      "35-70-6C-C9-A6-F6-67-27-78-D3-9D-D4-C1-5A-BD-11-3E-A6-D4-90-42-86-1C-EC-AC-C3-FD-5A-62-E6-17-8A")]
    public void Format1_Encode_matches_external_sha256_of_utf16le(string plaintext, string expected)
    {
        _hasher.Encode(plaintext, passwordFormat: 1, salt: null, systemEncryptionKey: null)
            .Should().Be(expected);
    }

    [Fact]
    public void Format1_output_is_always_95_chars()
    {
        var hex = _hasher.Encode("anything", 1, null, null);
        hex.Length.Should().Be(95);  // 32 bytes × 3 − 1 (no trailing hyphen)
        hex.Should().MatchRegex("^[0-9A-F]{2}(-[0-9A-F]{2}){31}$");
    }

    [Fact]
    public void Format1_Verify_matches_lowercase_stored_value()
    {
        // Novarad stores in citext; we should accept lowercase even though we emit uppercase.
        var upper = _hasher.Encode("password", 1, null, null);
        var lower = upper.ToLowerInvariant();
        _hasher.Verify("password", lower, 1, null, null).Should().BeTrue();
    }

    [Fact]
    public void Format1_ignores_salt_and_key()
    {
        // Format 1 is unsalted, so passing a salt or key should not change the result.
        var encoded = _hasher.Encode("password", 1, salt: new byte[16], systemEncryptionKey: "ignored");
        encoded.Should().Be("E2-01-06-5D-05-54-65-26-15-C3-20-C0-0A-1D-5B-C8-ED-CA-46-9D-72-C2-79-0E-24-15-2D-0C-1E-2B-61-89");
    }

    // -----------------------------------------------------------------------
    // Format 2 — AES-256-CBC (Rijndael) of UTF-16LE password
    //   key = SHA256(UTF-16LE(systemEncryptionKey))   (32 bytes)
    //   IV  = passwordSalt                            (16 bytes)
    //
    // Golden values from:
    //   python: AES.new(SHA256(UTF-16LE('TESTKEY...')), MODE_CBC, salt)
    //              .encrypt(pad(plaintext.encode('utf-16-le'), 16, 'pkcs7'))
    // -----------------------------------------------------------------------

    private const string TestSystemKey = "TESTKEY-do-not-use-in-prod";
    private static readonly byte[] TestSalt = new byte[]
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
    };

    [Theory]
    [InlineData("",            "7B-E3-9C-0A-32-DE-19-09-0C-94-5A-97-6D-02-05-85")]
    [InlineData("a",           "5F-92-14-21-BF-03-CF-B0-8F-3D-11-25-F7-C9-71-CF")]
    [InlineData("password",    "B2-12-19-60-CE-A1-2A-2C-60-AC-57-88-A3-02-8A-E6-EB-65-11-F0-DE-64-93-BB-5A-3D-49-7C-88-EC-C3-DD")]
    [InlineData("Novarad!23",  "48-A0-6A-CF-AD-A1-01-F9-3B-51-83-DC-AA-54-A4-E2-2E-F2-06-0D-40-E5-10-B1-13-40-25-AE-21-D7-BB-0A")]
    [InlineData("eclair",      "42-5D-C1-F5-7A-AB-A9-F3-27-3C-D5-B5-4D-13-37-F0")]
    public void Format2_Encode_matches_external_aes_vectors(string plaintext, string expected)
    {
        _hasher.Encode(plaintext, passwordFormat: 2, salt: TestSalt, systemEncryptionKey: TestSystemKey)
            .Should().Be(expected);
    }

    [Fact]
    public void Format2_throws_when_salt_is_null()
    {
        var act = () => _hasher.Encode("password", 2, salt: null, systemEncryptionKey: TestSystemKey);
        act.Should().Throw<ArgumentException>().WithMessage("*salt*");
    }

    [Fact]
    public void Format2_throws_when_salt_is_wrong_size()
    {
        var act = () => _hasher.Encode("password", 2, salt: new byte[8], systemEncryptionKey: TestSystemKey);
        act.Should().Throw<ArgumentException>().WithMessage("*16 bytes*");
    }

    [Fact]
    public void Format2_throws_when_system_key_is_missing()
    {
        var act = () => _hasher.Encode("password", 2, salt: TestSalt, systemEncryptionKey: null);
        act.Should().Throw<ArgumentException>().WithMessage("*EncryptionKey*");
    }

    [Fact]
    public void Format2_changing_salt_changes_output()
    {
        var a = _hasher.Encode("password", 2, salt: TestSalt, systemEncryptionKey: TestSystemKey);
        var differentSalt = new byte[16]; // all zeros — different from TestSalt
        var b = _hasher.Encode("password", 2, salt: differentSalt, systemEncryptionKey: TestSystemKey);
        a.Should().NotBe(b);
    }

    [Fact]
    public void Format2_changing_key_changes_output()
    {
        var a = _hasher.Encode("password", 2, salt: TestSalt, systemEncryptionKey: TestSystemKey);
        var b = _hasher.Encode("password", 2, salt: TestSalt, systemEncryptionKey: "different-key");
        a.Should().NotBe(b);
    }

    // -----------------------------------------------------------------------
    // Unsupported formats
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void Unknown_format_throws(int format)
    {
        var act = () => _hasher.Encode("anything", format, null, null);
        act.Should().Throw<NotSupportedException>().WithMessage("*Unsupported*");
    }

    [Fact]
    public void Verify_returns_false_when_format_is_unsupported()
    {
        // Verify should swallow NotSupportedException and return false rather than throw,
        // because the validator treats unknown-format users as auth failures with a logged warning.
        _hasher.Verify("password", "stored", passwordFormat: 99, salt: null, systemEncryptionKey: null)
            .Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // DecodeSalt
    // -----------------------------------------------------------------------

    [Fact]
    public void DecodeSalt_returns_null_for_null_or_empty()
    {
        _hasher.DecodeSalt(null).Should().BeNull();
        _hasher.DecodeSalt("").Should().BeNull();
    }

    [Fact]
    public void DecodeSalt_parses_hyphen_hex()
    {
        var bytes = _hasher.DecodeSalt("00-01-02-03-04-05-06-07-08-09-0A-0B-0C-0D-0E-0F");
        bytes.Should().BeEquivalentTo(TestSalt, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void DecodeSalt_parses_lowercase_hyphen_hex()
    {
        var bytes = _hasher.DecodeSalt("a3-7f-2c-01");
        bytes.Should().BeEquivalentTo(new byte[] { 0xA3, 0x7F, 0x2C, 0x01 }, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void DecodeSalt_falls_back_to_base64_when_no_hyphen()
    {
        // base64 of bytes 0..15
        var bytes = _hasher.DecodeSalt(Convert.ToBase64String(TestSalt));
        bytes.Should().BeEquivalentTo(TestSalt, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void DecodeSalt_then_Encode_format2_roundtrips()
    {
        // The validator pipeline reads password_salt as a string, decodes via DecodeSalt,
        // and passes the bytes into Encode/Verify. Make sure that chain works end-to-end.
        var saltString = "00-01-02-03-04-05-06-07-08-09-0A-0B-0C-0D-0E-0F";
        var salt = _hasher.DecodeSalt(saltString);
        var encoded = _hasher.Encode("password", 2, salt, TestSystemKey);
        encoded.Should().Be("B2-12-19-60-CE-A1-2A-2C-60-AC-57-88-A3-02-8A-E6-EB-65-11-F0-DE-64-93-BB-5A-3D-49-7C-88-EC-C3-DD");
    }

    // -----------------------------------------------------------------------
    // Verify — end-to-end glue
    // -----------------------------------------------------------------------

    [Fact]
    public void Verify_format1_round_trips()
    {
        var stored = _hasher.Encode("hunter2", 1, null, null);
        _hasher.Verify("hunter2", stored, 1, null, null).Should().BeTrue();
        _hasher.Verify("hunter3", stored, 1, null, null).Should().BeFalse();
    }

    [Fact]
    public void Verify_format2_round_trips()
    {
        var stored = _hasher.Encode("hunter2", 2, TestSalt, TestSystemKey);
        _hasher.Verify("hunter2", stored, 2, TestSalt, TestSystemKey).Should().BeTrue();
        _hasher.Verify("hunter3", stored, 2, TestSalt, TestSystemKey).Should().BeFalse();
    }
}
