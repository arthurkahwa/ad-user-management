namespace UserMgmt.Core.Common;

/// <summary>
/// A page of items plus the metadata callers need to render pagers and prefetch.
/// </summary>
/// <param name="Items">The items in this page.</param>
/// <param name="Page">The 1-based page index.</param>
/// <param name="PageSize">The maximum number of items per page.</param>
/// <param name="TotalCount">The total number of items across all pages.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
