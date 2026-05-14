using UserMgmt.Core.Auth;

namespace UserMgmt.Core.Tests.Fixtures;

/// <summary>
/// Lightweight <see cref="ICurrentActor"/> stub for tests that don't need a mock.
/// </summary>
public sealed class StubCurrentActor : ICurrentActor
{
    /// <summary>Create a stub with a fixed actor.</summary>
    public StubCurrentActor(Actor actor)
    {
        Current = actor;
    }

    /// <inheritdoc />
    public Actor Current { get; set; }
}
