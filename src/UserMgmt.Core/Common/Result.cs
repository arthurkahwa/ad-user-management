using System.Diagnostics.CodeAnalysis;

namespace UserMgmt.Core.Common;

/// <summary>
/// A hand-rolled, allocation-friendly discriminated union for service operations
/// that can succeed with a typed value or fail with a typed error.
/// </summary>
/// <remarks>
/// Not built on an external library to keep the core dependency footprint minimal.
/// Callers should prefer the <see cref="Match{TOut}"/> projection over reading
/// <see cref="Value"/> / <see cref="Error"/> directly so the discriminator is
/// always respected.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Result<T,TError>.Success / .Failure are the canonical factory pattern for hand-rolled result unions.")]
public readonly record struct Result<T, TError>
{
    /// <summary>The success value. Null when <see cref="IsSuccess"/> is false.</summary>
    public T? Value { get; }

    /// <summary>The failure value. Null when <see cref="IsSuccess"/> is true.</summary>
    public TError? Error { get; }

    /// <summary>True if this result carries a success value.</summary>
    public bool IsSuccess { get; }

    private Result(T value)
    {
        Value = value;
        Error = default;
        IsSuccess = true;
    }

    private Result(TError error)
    {
        Value = default;
        Error = error;
        IsSuccess = false;
    }

    /// <summary>Wrap a success value.</summary>
    public static Result<T, TError> Success(T value) => new(value);

    /// <summary>Wrap a failure value.</summary>
    public static Result<T, TError> Failure(TError error) => new(error);

    /// <summary>Project the union into a single value.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(Value!) : onFailure(Error!);
    }
}
