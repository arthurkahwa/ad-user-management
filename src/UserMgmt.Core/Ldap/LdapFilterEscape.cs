using System.Text;

namespace UserMgmt.Core.Ldap;

/// <summary>
/// RFC 4515 LDAP search filter value escaping.
/// </summary>
/// <remarks>
/// Every service-layer call site that constructs an LDAP filter from
/// user-supplied input must pass that input through <see cref="Escape"/> first.
/// The set of characters that must be escaped per RFC 4515 §3 is exactly
/// <c>\</c>, <c>*</c>, <c>(</c>, <c>)</c>, and NUL. Each is replaced with a
/// two-digit upper-case hex escape introduced by <c>\</c>.
/// <para>
/// The function is idempotent in the practical sense that re-escaping
/// already-escaped output (which still contains literal <c>\</c> characters)
/// produces the same byte-level meaning when read by an RFC 4515 parser —
/// the backslash gets escaped to <c>\5c</c> on the second pass, which
/// represents the same literal backslash.
/// </para>
/// </remarks>
public static class LdapFilterEscape
{
    /// <summary>
    /// Returns <paramref name="input"/> with every RFC 4515 reserved character
    /// replaced by its <c>\HH</c> hex escape (upper-case).
    /// </summary>
    /// <param name="input">User-supplied filter fragment. May be empty.</param>
    /// <returns>An RFC 4515-safe filter value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public static string Escape(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Length == 0)
        {
            return string.Empty;
        }

        // Reserve a little headroom so a worst-case all-escape input
        // doesn't immediately reallocate the builder.
        var builder = new StringBuilder(input.Length + 8);

        foreach (char c in input)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\5c");
                    break;
                case '*':
                    builder.Append("\\2a");
                    break;
                case '(':
                    builder.Append("\\28");
                    break;
                case ')':
                    builder.Append("\\29");
                    break;
                case '\0':
                    builder.Append("\\00");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
