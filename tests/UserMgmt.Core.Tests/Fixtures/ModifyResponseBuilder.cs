using System.DirectoryServices.Protocols;
using System.Reflection;

namespace UserMgmt.Core.Tests.Fixtures;

/// <summary>
/// Builds <see cref="ModifyResponse"/> and
/// <see cref="DirectoryOperationException"/> instances for AdService update
/// tests. The .NET BCL keeps all relevant constructors internal, so this
/// helper uses reflection — the same trick <see cref="SearchResultEntryBuilder"/>
/// uses, narrowly scoped to one BCL version.
/// </summary>
internal static class ModifyResponseBuilder
{
    private static readonly ConstructorInfo ModifyResponseCtor =
        typeof(ModifyResponse).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(DirectoryControl[]), typeof(ResultCode), typeof(string), typeof(Uri[])],
            modifiers: null)
        ?? throw new InvalidOperationException("ModifyResponse(...) constructor not found.");

    /// <summary>Build a <see cref="ModifyResponse"/> with a success result code.</summary>
    public static ModifyResponse BuildSuccess(string? matchedDn = null)
    {
        return (ModifyResponse)ModifyResponseCtor.Invoke([
            matchedDn ?? string.Empty,
            Array.Empty<DirectoryControl>(),
            ResultCode.Success,
            null!,
            Array.Empty<Uri>(),
        ]);
    }

    /// <summary>
    /// Build a <see cref="DirectoryOperationException"/> whose <c>Response</c>
    /// carries the supplied <see cref="ResultCode"/>. The service inspects
    /// <c>ex.Response.ResultCode</c> to detect CAS misses.
    /// </summary>
    public static DirectoryOperationException BuildOperationException(ResultCode resultCode, string? message = null)
    {
        ModifyResponse response = (ModifyResponse)ModifyResponseCtor.Invoke([
            string.Empty,
            Array.Empty<DirectoryControl>(),
            resultCode,
            message ?? resultCode.ToString(),
            Array.Empty<Uri>(),
        ]);

        // DirectoryOperationException(DirectoryResponse, string) is public.
        return new DirectoryOperationException(response, message ?? resultCode.ToString());
    }
}
