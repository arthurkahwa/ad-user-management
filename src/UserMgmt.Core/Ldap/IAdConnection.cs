using System.DirectoryServices.Protocols;

namespace UserMgmt.Core.Ldap;

/// <summary>
/// Abstraction over an LDAP transport. Service-layer code constructs typed
/// <see cref="DirectoryRequest"/> instances and submits them through this
/// surface; the concrete implementation owns connection pooling, binding,
/// and async marshalling.
/// </summary>
/// <remarks>
/// The interface intentionally exposes <see cref="Port"/> (rather than the
/// underlying <c>LdapConnection</c>) so password-sensitive service operations
/// can enforce LDAPS without coupling to the directory client library.
/// </remarks>
public interface IAdConnection
{
    /// <summary>The TCP port bound by the underlying connection.</summary>
    /// <remarks>
    /// <c>AdService.ResetPasswordAsync</c> and <c>AdService.CreateAsync</c>
    /// inspect this value and throw <see cref="LdapsRequiredException"/> when
    /// it is not 636.
    /// </remarks>
    int Port { get; }

    /// <summary>Submit a paged or non-paged search request.</summary>
    /// <param name="request">The search request to execute.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>The full <see cref="SearchResponse"/> returned by the server.</returns>
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submit an add request.</summary>
    Task<AddResponse> AddAsync(AddRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submit a modify request.</summary>
    Task<ModifyResponse> ModifyAsync(ModifyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Submit a delete request.</summary>
    Task<DeleteResponse> DeleteAsync(DeleteRequest request, CancellationToken cancellationToken = default);
}
