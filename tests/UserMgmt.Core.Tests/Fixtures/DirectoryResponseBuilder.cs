using System.DirectoryServices.Protocols;
using System.Reflection;

namespace UserMgmt.Core.Tests.Fixtures;

/// <summary>
/// Builds non-search <see cref="DirectoryResponse"/> instances
/// (<see cref="ModifyResponse"/>, <see cref="AddResponse"/>,
/// <see cref="DeleteResponse"/>) for tests that need a stub
/// <see cref="System.DirectoryServices.Protocols"/> response without
/// going through a real LDAP server.
/// </summary>
/// <remarks>
/// All response constructors on the BCL types are internal, so reflection
/// is the only path. Mirrors the pattern in
/// <see cref="SearchResultEntryBuilder"/>.
/// </remarks>
internal static class DirectoryResponseBuilder
{
    /// <summary>Build a <typeparamref name="TResponse"/> with <see cref="ResultCode.Success"/>.</summary>
    public static TResponse BuildSuccess<TResponse>()
        where TResponse : DirectoryResponse
        => Build<TResponse>(ResultCode.Success, errorMessage: null);

    /// <summary>Build a <typeparamref name="TResponse"/> with the supplied result code.</summary>
    public static TResponse Build<TResponse>(ResultCode resultCode, string? errorMessage = null)
        where TResponse : DirectoryResponse
    {
        ConstructorInfo ctor = typeof(TResponse).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(DirectoryControl[]), typeof(ResultCode), typeof(string), typeof(Uri[])],
            modifiers: null)
            ?? throw new InvalidOperationException(
                $"No 5-arg internal constructor on {typeof(TResponse).Name}.");

        return (TResponse)ctor.Invoke([null!, Array.Empty<DirectoryControl>(), resultCode, errorMessage!, Array.Empty<Uri>()]);
    }
}
