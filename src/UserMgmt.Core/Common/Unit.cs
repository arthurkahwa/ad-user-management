namespace UserMgmt.Core.Common;

/// <summary>
/// Empty-payload sentinel used as the success type of a <see cref="Result{T, TError}"/>
/// when the operation has no value to return but still needs a typed result for
/// the failure side.
/// </summary>
/// <remarks>
/// Implemented as a singleton struct so <c>Result&lt;Unit, TError&gt;.Success(Unit.Value)</c>
/// allocates nothing on the heap and reads naturally at call sites.
/// </remarks>
public readonly record struct Unit
{
    /// <summary>The single inhabitant of <see cref="Unit"/>.</summary>
    public static Unit Value { get; }
}
