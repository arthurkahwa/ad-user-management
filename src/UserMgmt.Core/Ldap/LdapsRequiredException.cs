namespace UserMgmt.Core.Ldap;

/// <summary>
/// Thrown when a password-sensitive operation runs against a non-LDAPS connection.
/// <c>AdService.ResetPasswordAsync</c> and <c>AdService.CreateAsync</c> check
/// <c>IAdConnection.Port</c> and raise this exception when the bound port is not 636.
/// </summary>
public sealed class LdapsRequiredException : Exception
{
    /// <summary>Default constructor with the canonical message.</summary>
    public LdapsRequiredException()
        : base("Operation requires LDAPS (port 636); the bound connection is not LDAPS.")
    {
    }

    /// <summary>Constructor with a custom message.</summary>
    public LdapsRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>Constructor with a custom message and an inner exception.</summary>
    public LdapsRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
