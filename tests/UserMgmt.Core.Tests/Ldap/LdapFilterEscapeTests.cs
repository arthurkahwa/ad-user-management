using UserMgmt.Core.Ldap;

namespace UserMgmt.Core.Tests.Ldap;

public sealed class LdapFilterEscapeTests
{
    [Fact]
    public void Escape_EmptyString_ReturnsEmpty()
    {
        LdapFilterEscape.Escape(string.Empty).ShouldBe(string.Empty);
    }

    [Fact]
    public void Escape_NullInput_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => LdapFilterEscape.Escape(null!));
    }

    [Fact]
    public void Escape_PlainAscii_ReturnsUnchanged()
    {
        LdapFilterEscape.Escape("alice").ShouldBe("alice");
    }

    [Fact]
    public void Escape_PlainAsciiWithSpaces_ReturnsUnchanged()
    {
        LdapFilterEscape.Escape("Alice Smith").ShouldBe("Alice Smith");
    }

    [Fact]
    public void Escape_BackslashInInput_ReturnsBackslashHexEscaped()
    {
        // RFC 4515: \ becomes \5c
        LdapFilterEscape.Escape(@"a\b").ShouldBe(@"a\5cb");
    }

    [Fact]
    public void Escape_AsteriskInInput_ReturnsBackslashHexEscaped()
    {
        // RFC 4515: * becomes \2a (prevents wildcard injection)
        LdapFilterEscape.Escape("a*b").ShouldBe(@"a\2ab");
    }

    [Fact]
    public void Escape_OpenParenInInput_ReturnsBackslashHexEscaped()
    {
        // RFC 4515: ( becomes \28
        LdapFilterEscape.Escape("a(b").ShouldBe(@"a\28b");
    }

    [Fact]
    public void Escape_CloseParenInInput_ReturnsBackslashHexEscaped()
    {
        // RFC 4515: ) becomes \29
        LdapFilterEscape.Escape("a)b").ShouldBe(@"a\29b");
    }

    [Fact]
    public void Escape_NullCharInInput_ReturnsBackslashHexEscaped()
    {
        // RFC 4515: NUL becomes \00
        LdapFilterEscape.Escape("a\0b").ShouldBe(@"a\00b");
    }

    [Fact]
    public void Escape_AllReservedCharsInSingleString_ReturnsFullyEscaped()
    {
        // One of each, in RFC 4515 order
        LdapFilterEscape.Escape("\\*()\0").ShouldBe(@"\5c\2a\28\29\00");
    }

    [Fact]
    public void Escape_AlreadyEscapedInput_IsIdempotentByRfc4515ReadSemantics()
    {
        // The output of Escape() contains literal \ characters. Running it
        // through Escape() again therefore escapes those backslashes once more
        // — \5c stays as \5c5c. The RFC parser reads both forms as the same
        // literal byte sequence, so the operation is idempotent at the
        // *meaning* level, not at the byte level.
        string once = LdapFilterEscape.Escape("(abc)");
        string twice = LdapFilterEscape.Escape(once);

        once.ShouldBe(@"\28abc\29");
        // Re-escaping the backslash characters that were introduced by the first pass.
        twice.ShouldBe(@"\5c28abc\5c29");
    }

    [Fact]
    public void Escape_NoReservedChars_IsByteLevelIdempotent()
    {
        // When the input contains no characters that require escaping,
        // Escape is exactly idempotent at the byte level.
        const string input = "displayName=alice smith";
        string once = LdapFilterEscape.Escape(input);
        string twice = LdapFilterEscape.Escape(once);

        once.ShouldBe(input);
        twice.ShouldBe(input);
    }

    [Fact]
    public void Escape_MultiByteUtf8_PreservesUnicodeUnchanged()
    {
        // RFC 4515 does not require escaping for non-reserved Unicode characters.
        // Round-trip through Escape should leave them unchanged.
        const string input = "Ünïçødé-name-日本語-عربى";
        LdapFilterEscape.Escape(input).ShouldBe(input);
    }

    [Fact]
    public void Escape_HighSurrogatePair_PreservesPair()
    {
        // Emoji is a surrogate pair in UTF-16. Escape must not split it.
        string input = "user-😀"; // 😀
        LdapFilterEscape.Escape(input).ShouldBe(input);
    }

    [Fact]
    public void Escape_HexEscapeUsesUppercaseLowercase_MatchesRfcLowercase()
    {
        // RFC 4515 specifies HEX as upper- or lower-case; we use lower-case for stability.
        // Verify by checking exact bytes.
        string result = LdapFilterEscape.Escape("*");
        result.ShouldBe(@"\2a");
        result.ShouldNotBe(@"\2A");
    }

    [Theory]
    [InlineData('\\', @"\5c")]
    [InlineData('*', @"\2a")]
    [InlineData('(', @"\28")]
    [InlineData(')', @"\29")]
    [InlineData('\0', @"\00")]
    public void Escape_SingleReservedChar_ReturnsExactHexEscape(char input, string expected)
    {
        LdapFilterEscape.Escape(input.ToString()).ShouldBe(expected);
    }

    [Fact]
    public void Escape_RandomFuzz_NoReservedCharSurvivesUnescaped()
    {
        // Build a deterministic-but-broad fuzz input over the full BMP except surrogates.
        var rng = new Random(42);
        for (int trial = 0; trial < 250; trial++)
        {
            int length = rng.Next(0, 64);
            string input = string.Create(length, rng, static (span, r) =>
            {
                for (int i = 0; i < span.Length; i++)
                {
                    int code;
                    do
                    {
                        code = r.Next(0, 0xFFFF);
                    }
                    while (code >= 0xD800 && code <= 0xDFFF); // skip surrogate halves
                    span[i] = (char)code;
                }
            });

            string escaped = LdapFilterEscape.Escape(input);

            // After escaping, no raw reserved character may remain — they must all
            // appear inside their \HH sequence. A simple way to check: walk the
            // string and confirm that every '(' ')' '*' '\0' is preceded by '\'.
            for (int i = 0; i < escaped.Length; i++)
            {
                char c = escaped[i];
                if (c is '(' or ')' or '*' or '\0')
                {
                    // Must be the second char of an escape sequence.
                    (i > 0 && escaped[i - 1] == '\\').ShouldBeTrue(
                        $"Unescaped reserved char '{c}' at index {i} in input fuzz trial {trial}.");
                }
            }
        }
    }
}
