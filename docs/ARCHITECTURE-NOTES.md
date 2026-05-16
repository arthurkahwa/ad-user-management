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
    string? Mail,
    string? TelephoneNumber,
    string? PhysicalDeliveryOfficeName,
    DateTime? AccountExpires);
```

`NewUserDto` carries only AD-resident fields. The journal-specific fields (`AnredeId`, `AcademicTitleId`, `Kuerzel`, `Rechner`, the 20+ onboarding-checklist booleans, the five `Notes*` fields, and the three third-party-account fields) are passed via a separate `NewJournalDto` consumed by `IJournalService.CreateJournalAsync`. The journal service internally splits the inputs: AD fields → `IAdService.CreateAsync`, journal fields → `IAttributeService.UpsertAsync` on `UserJournalRecord`. See [Hybrid storage policy](#hybrid-storage-policy-for-userjournal).

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

## Hybrid storage policy for `UserJournal`

Active Directory and the sidecar SQL database (`MADB`) split storage responsibility along data-shape lines. AD is the system of record for identity attributes that have native, well-typed homes in the schema; the sidecar is the system of record for application-specific data that AD cannot store cleanly. The AD schema is never extended.

### Fields stored in Active Directory

These 12 attributes live in AD and are read live on every journal load:

| Journal field | AD attribute | Notes |
|---|---|---|
| `Upn` | `userPrincipalName` | Identity key |
| `Aduser` | `sAMAccountName` | Logon name |
| `Vorname` | `givenName` | First name |
| `Nachname` | `sn` | Surname |
| `Kuerzel` | `initials` | Short code, max 6 chars in AD |
| `Sid` | `objectSid` | Read-only system identity |
| `AcademicTitle.Title` | `personalTitle` | Mirrored from the lookup row at write time |
| `EMail` | `mail` | Primary email |
| `TelNr` | `telephoneNumber` | Phone |
| `Platz` | `physicalDeliveryOfficeName` | Seat/office location |
| `ZeitBis` | `accountExpires` | Native AD account expiration; kept in lockstep with the journal record |
| `IsActive` (inverse) | `userAccountControl & ACCOUNTDISABLE` | Account enabled state |

### Fields stored in SQL (`UserJournalRecord` on `MADB`)

Everything else: `AnredeId`, `AcademicTitleId` (FK columns), `Rechner`, `Notes01`–`Notes05`, the 20+ onboarding-checklist booleans (`AdAccountAnlegen`, `MailpostfachErstellen`, `OpenVpnZertifikat`, …), the three third-party-account fields (Autodesk / Adobe / Solibri with `Konto*` text + `*Erstellt` boolean), and the `RowVersion` concurrency token.

### Read and write paths

- **Read.** `JournalService.GetJournalAsync(upn)` calls `IAdService.GetAsync(upn)` (AD) and `IAttributeService.GetAsync(upn)` (SQL) and merges the results into a `UserJournal` view-model.
- **Write to an AD-resident field** (e.g. `Vorname`) routes through `IAdService.UpdateAsync` with LDAP attribute-level CAS (delete-old + add-new on `givenName`). SQL untouched.
- **Write to a SQL-resident field** (e.g. ticking `OpenVpnZertifikat`) routes through `IAttributeService.UpdateChecklistAsync` (or `UpsertAsync` for bulk edits) with the `Guid` concurrency token. AD untouched.
- **Cross-store writes** — Create Journal, and `ZeitBis` ↔ `accountExpires` sync — follow AD-first, SQL-second, with partial-state recovery via the `ReconciliationQueue` (see the cross-store consistency section in the README).

### Why not extend the AD schema for the SQL-resident fields

Schema extensions are operationally irreversible — Microsoft does not support removing custom attributes once `schemaUpdateNow` has propagated. The application data has the wrong shape for AD anyway (booleans on a checklist, multiple long-text note fields, lookup foreign keys), and the customer's AD forest belongs to the customer, not the application. The hybrid model keeps AD pristine.

## Optimistic concurrency on `UserJournalRecord`

`UserJournalRecord.RowVersion` is a `Guid` concurrency token that the application bumps on every write — **not** a SQL Server `rowversion` byte array. SQL Server's `rowversion` is server-generated and monotonically bumped, but SQLite (which the test fixture uses) has no equivalent type and won't auto-bump a `byte[]` column the same way; the M1.1 PR therefore deferred the real concurrency test. Switching to an app-managed `Guid` makes the mechanism behave identically across SQL Server, SQLite, and EF Core's in-memory provider — the property is configured with `.IsConcurrencyToken()` on the model, `AttributeService` writes a fresh `Guid.NewGuid()` before each `SaveChangesAsync`, and EF Core's standard `DbUpdateConcurrencyException` path surfaces stale-token attempts to update. The property is marked `[AuditIgnore]` so the audit log doesn't record token rotations. Callers exchange tokens as `Guid?` rather than `byte[]?` at the service boundary. The original `byte[] RowVersion` column shape in the `InitialCreate` migration is altered to `uniqueidentifier` by the follow-up `ChangeRowVersionToGuidConcurrencyToken` migration. The entity was renamed from `UserAttributes` to `UserJournalRecord` during the journal-domain pivot; the column shape and concurrency behaviour are unchanged.

## Migration conventions

- **EF Core migrations** live in `src/UserMgmt.Data/Migrations/`. Use `dotnet ef migrations add` to scaffold.
- **DENY UPDATE / DELETE on AuditEntry** runs in the migration's `Up` via `migrationBuilder.Sql(...)`. The GRANT target is configurable via the `UserMgmt:AppPrincipal` config key (default `CURRENT_USER` for local dev; in production, supplied via deploy-time configuration).
- **Migration naming**: `<Verb>_<Subject>` (e.g. `Add_AuditEntryAppendOnly`, `Rename_UserAttributes_To_UserJournalRecord`).

## Partial-class ownership map for `AdService`

`AdService` is declared `partial` across multiple files (one per operation family). When several agents extend a `partial` class in parallel, two failure modes emerge that git's textual merge cannot detect:

1. **Duplicate field declarations.** Two partial files independently declare the same private field (e.g. `_auditService`). C# rejects this at build time, but only after the merge has landed on `main`.
2. **Duplicate constant declarations** inside nested `partial` helper classes (e.g. `AdAttributes`). Same failure mode at the `partial static class` level.

Both fired during the M1 wave-2 dispatch and required a fix-forward commit on `main`. The rule below prevents recurrence: **each shared field, logger, and nested-class constant has exactly one declaring file; other partials reference it without redeclaring.**

### Current ownership

| File | Owns (declares) | References (uses but does not declare) |
|---|---|---|
| `AdService.cs` | `_connection`, `_options`, `_logger`; the read-path 3-arg constructor; the `AdAttributes` partial root with read-path constants (`SamAccountName`, `UserPrincipalName`, `DistinguishedName`, `DisplayName`, `GivenName`, `Surname`, `Department`, `Manager`, `WhenCreated`, `LastLogon`, `UserAccountControl`) | — |
| `AdService.Create.cs` | `_attributeService`, `_auditService`, `_reconciliationQueue`; the 6-arg write-path constructor; `AdAttributes` constants `UnicodePwd` and `PwdLastSet`; helpers `IsOuWhitelisted`, `UpnExistsAsync` | `_connection`, `_options`, `_logger`, `_currentActor` |
| `AdService.Update.cs` | `AttributeRoutes` dispatch table; `LogAdAttributeConflict` logger; the 5-arg constructor overload (chains to read-path) | `_connection`, `_attributeService`, `_auditService` |
| `AdService.Lifecycle.cs` | `LogPasswordResetSucceeded`, `LogSetEnabledSucceeded` loggers; `AccountDisableBit`, `LdapsPort` constants; the 4-arg constructor overload (chains to read-path); helpers `GetDnAndUacAsync`, `IsCasFailure`, `RecordEnableAuditAsync`, `RecordPasswordResetAuditAsync` | `_connection`, `_auditService`, `UnicodePwd` / `PwdLastSet` from `AdService.Create.cs` |
| `AdService.Groups.cs` | `_currentActor`; `MemberAttribute` constant; `GroupAttributes`, `UserDnOnlyAttributes` arrays; the 5-arg constructor overload (chains to read-path); helpers `ResolveUserDnAsync`, `ResolveGroupAsync`, `GroupContainsMember`, `RecordMembershipAuditAsync` | `_connection`, `_auditService` |

### Rule for future partials

When adding a new partial file (e.g. for a new operation family in M2 / M3):

1. **Before declaring any private field**, grep across all `AdService.*.cs` files. If the field already exists, use it; do not redeclare.
2. **Before declaring a nested-class constant** (especially inside `AdAttributes`), grep for the constant. Same rule.
3. **Constructor overloads with distinct parameter lists coexist freely.** A new constructor that initialises an already-declared field is expected, not a duplicate.
4. **If a shared dependency genuinely doesn't exist yet**, add it to the file whose concern is closest (typically `AdService.Create.cs` since it owns the canonical write-path constructor). Then extend the table above so the next contributor finds it.

The same rule applies to any other class that becomes `partial` across files in this codebase. The table here is the canonical reference for `AdService`; sibling tables should be added to this section as needed.

## Repository structure (M1 only — other projects added when their milestone starts)

```
src/
  UserMgmt.Core/             Domain types, abstractions, journal orchestrator
    Auth/                    ICurrentActor, Actor, SystemActor, AuditIgnoreAttribute
    Common/                  Result, PagedResult, Unit, error records
    Domain/                  AdUser, NewUserDto, UserJournal (view-model), OnboardingChecklist, Notes, ThirdPartyAccounts, AcademicTitle, Anrede
    Ldap/                    IAdConnection, AdConnection, LdapFilterEscape, LdapsRequiredException
    Services/                IAdService + AdService (partial files), IAttributeService, IAuditService, IJournalService + JournalService
    Validation/              FluentValidation validators
  UserMgmt.Data/             EF Core
    UserMgmtDbContext.cs
    Entities/                UserJournalRecord, AuditEntry, ReconciliationQueue, AppLog
    Services/                AttributeService, AuditService (depend on UserMgmtDbContext; interface lives in Core)
    Interceptors/            AuditSaveChangesInterceptor
    Migrations/              EF Core migrations (auto-generated)
tests/
  UserMgmt.Core.Tests/       xUnit + NSubstitute + Shouldly + SQLite-in-memory
    Services/                AdServiceTests (per-operation partials), AttributeServiceTests, AuditServiceTests, JournalServiceTests
    Ldap/                    LdapFilterEscapeTests
    Fixtures/                SqliteDbContextFixture, StubCurrentActor, SearchResultEntryBuilder, ModifyResponseBuilder, DirectoryResponseBuilder, CapturingLogger
```

## What this document is NOT

- Not the spec. The README is. Read that first.
- Not the issue tracker. Each M1 slice has its own GitHub issue with acceptance criteria.
- Not a substitute for ADRs (which will live in `docs/adr/` once decisions warrant their own files).
