# Architecture Notes

Cross-cutting decisions and type shapes shared across all M1 issues. Agents implementing slices should treat this document as authoritative — when an issue body and this document agree, follow them; if they disagree, this document is the resolution and the issue should be updated.

The README is the high-level spec. This document is the implementation-level conventions agents and reviewers reference when writing code.

---

## Target framework and tooling

- **Target framework**: `net10.0` (set in `Directory.Build.props`; agents should not override per-project).
- **SDK**: pinned in `global.json` with `rollForward: latestFeature`.
- **Language version**: `latest` (C# 14+).
- **Nullable reference types**: enabled and treated as errors. Public API surfaces must annotate nullability correctly.
- **Implicit usings**: enabled. Add to `<Using Include="..." />` in `.csproj` for project-wide using imports.
- **Treat warnings as errors**: enabled. Suppressions are case-by-case via `NoWarn` in `Directory.Build.props` or per-project.

## Package management

- **Central Package Management** via `Directory.Packages.props`. `PackageReference` items in `.csproj` files must NOT specify `Version=`; versions live in the central props file.
- **Transitive pinning**: `CentralPackageTransitivePinningEnabled=true`. CVE-driven pins of transitive deps live in the central file (currently `System.Security.Cryptography.Xml`).

## Test framework conventions

- **Framework**: xUnit v2.9.3 + `xunit.runner.visualstudio` v3.1.4.
- **Mocking**: NSubstitute (no Moq, no FakeItEasy).
- **Assertions**: Shouldly (no FluentAssertions — v8+ moved to a paid licence and is avoided here).
- **DB-backed tests**: SQLite in-memory connection per test. The EF Core in-memory provider is NOT used because it does not enforce `CHECK` constraints or `RowVersion` semantics, both of which we rely on.
- **Test class naming**: `<ClassUnderTest>Tests` (e.g. `AdServiceTests`, `LdapFilterEscapeTests`).
- **Test method naming**: `MethodName_Scenario_ExpectedOutcome` (e.g. `CreateAsync_OuNotInWhitelist_ThrowsOuNotAllowedException`).

## Common type shapes (load-bearing — pin these in M1.1)

These types are referenced from multiple M1 slices. Defining them inconsistently between slices breaks integration.

### `Result<T, TError>`

A minimal hand-rolled discriminated-union, not an external library. Shape:

```csharp
public readonly record struct Result<T, TError>
{
    public T? Value { get; }
    public TError? Error { get; }
    public bool IsSuccess { get; }

    private Result(T value) { Value = value; IsSuccess = true; }
    private Result(TError error) { Error = error; IsSuccess = false; }

    public static Result<T, TError> Success(T value) => new(value);
    public static Result<T, TError> Failure(TError error) => new(error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TError, TOut> onFailure) =>
        IsSuccess ? onSuccess(Value!) : onFailure(Error!);
}
```

A non-generic `Result<TError>` (Unit-on-success) and an `OperationResult` alias may be added as needed.

### `PagedResult<T>`

```csharp
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
```

### `AdUser` (domain type, returned from `IAdService.SearchAsync` / `GetAsync`)

```csharp
public sealed record AdUser(
    string Upn,
    string SamAccountName,
    string Dn,
    string DisplayName,
    string? GivenName,
    string? Surname,
    string? Department,
    string? ManagerDn,
    string OuPath,
    DateTime WhenCreated,
    DateTime? LastLogon,
    bool Enabled);
```

`Enabled` is computed by the service from `userAccountControl & ACCOUNTDISABLE == 0` — callers see a clean bool, not the bitfield.

### `NewUserDto` (input to `IAdService.CreateAsync`)

```csharp
public sealed record NewUserDto(
    string Upn,
    string SamAccountName,
    string GivenName,
    string Surname,
    string DisplayName,
    string OuPath,
    string? Department,
    string? ManagerDn,
    // Sidecar attributes (persisted in UserMgmt.Data, not AD)
    string? CostCenter,
    string? ContractType,
    string? EmployeeId);
```

### `Actor` (returned by `ICurrentActor`)

```csharp
public sealed record Actor(string Upn, ActorSource Source)
{
    public bool IsSystem => Source is ActorSource.System or ActorSource.MLRetrain;
}

public enum ActorSource { Web, Api, MLRetrain, System }
```

`ICurrentActor` exposes the `Actor` directly:

```csharp
public interface ICurrentActor
{
    Actor Current { get; }
}
```

A `SystemActor` static instance is provided for background services (`MLTrainer`, `ReconciliationWorker`):

```csharp
public static class SystemActor
{
    public static Actor Instance { get; } = new("system@local", ActorSource.System);
}
```

### Error types (shared across services)

```csharp
public sealed record ConcurrencyConflict(string Attribute, string? CurrentValue);

public sealed record OuNotAllowed(string OuPath);

public sealed record UpnAlreadyExists(string Upn);

public sealed record UserNotFound(string Upn);

public sealed record PartialSuccess<T>(T Value, string SidecarFailureReason);
```

Service result types compose these — e.g. `Result<AdUser, CreateUserError>` where `CreateUserError` is a tagged union (sealed record hierarchy or `OneOf<...>`-shaped) of `OuNotAllowed`, `UpnAlreadyExists`, `PartialSuccess<AdUser>`.

## LDAPS-only enforcement

`IAdConnection.Port` exposes the bound port (`int`). `AdService.ResetPasswordAsync` and `AdService.CreateAsync` check this property and throw `LdapsRequiredException` if the value is not `636`. This lets us enforce LDAPS at the service layer without exposing `LdapConnection` internals.

`LdapsRequiredException` lives in `UserMgmt.Core/Ldap/` and is a simple `Exception` subclass.

## Migration conventions

- **EF Core migrations** live in `src/UserMgmt.Data/Migrations/`. Use `dotnet ef migrations add` to scaffold.
- **DENY UPDATE / DELETE on AuditEntry** runs in the migration's `Up` via `migrationBuilder.Sql(...)`. The GRANT target is configurable via the `UserMgmt:AppPrincipal` config key (default `CURRENT_USER` for local dev; in production, supplied via deploy-time configuration).
- **Migration naming**: `<Verb>_<Subject>` (e.g. `Add_AuditEntryAppendOnly`, `Update_UserAttributes_AddCostCenter`).

## Repository structure (M1 only — other projects added when their milestone starts)

```
src/
  UserMgmt.Core/             Domain types, services, abstractions
    Auth/                    ICurrentActor, Actor, SystemActor
    Common/                  Result, PagedResult, error types
    Domain/                  AdUser, NewUserDto
    Ldap/                    IAdConnection, AdConnection, LdapFilterEscape, LdapsRequiredException
    Services/                IAdService, AdService, IAttributeService, AttributeService, IAuditService, AuditService
    Validation/              FluentValidation validators
  UserMgmt.Data/             EF Core
    UserMgmtDbContext.cs
    Entities/                UserAttributes, AuditEntry, ReconciliationQueue, AppLog
    Interceptors/            AuditSaveChangesInterceptor
    Migrations/              EF Core migrations (auto-generated)
tests/
  UserMgmt.Core.Tests/       xUnit
    Services/                AdServiceTests, AttributeServiceTests, AuditServiceTests
    Ldap/                    LdapFilterEscapeTests
    Fixtures/                SqliteDbContextFixture, ICurrentActorStub
```

## What this document is NOT

- Not the spec. The README is. Read that first.
- Not the issue tracker. Each M1 slice has its own GitHub issue with acceptance criteria.
- Not a substitute for ADRs (which will live in `docs/adr/` once decisions warrant their own files).
