# AD User Management

> Dual-platform Active Directory administration for the browser and iPad, with on-premises ML-driven stale account detection.

![Language](https://img.shields.io/badge/language-C%23%20%7C%20Swift-239120?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20iPadOS%2017%2B-2f80ed?style=flat-square)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?style=flat-square)
![SwiftUI](https://img.shields.io/badge/SwiftUI-Native-fa7343?style=flat-square)
![ML.NET](https://img.shields.io/badge/ML.NET-BinaryClassification-8a2be2?style=flat-square)
![Compliance](https://img.shields.io/badge/compliance-GDPR%20%7C%20ISO%2027001-0a7f3f?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)
![Status](https://img.shields.io/badge/status-Planned-orange?style=flat-square)

---

## Overview

**AD User Management** is a self-hosted enterprise application for administering Microsoft Active Directory user accounts. It pairs an ASP.NET Core Razor Pages web interface with a native SwiftUI iPad app — both sharing a single service layer — and augments traditional CRUD with an **ML.NET stale account predictor** that runs entirely on-premises.

The product is designed for **IT administrators, helpdesks, and CISOs** at organisations that keep identity on-site, run Exchange on-prem, and need a modern administrative experience without sending employee data to the cloud. It replaces a patchwork of Active Directory Users and Computers (ADUC), PowerShell scripts, and spreadsheets with a governed, auditable, compliance-ready tool that maps directly to **GDPR** lawful-basis processing and **ISO/IEC 27001:2022** Annex A controls.

Key differentiators:

- **Two first-class clients, one backend.** The Razor Pages UI and the SwiftUI iPad app talk to the same service layer — nothing is re-implemented twice.
- **Intelligent account hygiene.** ML.NET predicts which accounts are likely to become stale, retrains nightly from the app's own audit trail, and exposes scores as a sortable column and filter.
- **Compliance by construction.** Append-only audit log enforced at the database layer, opt-out flag for automated profiling, LDAPS-only password operations, and a DPIA-ready data model.

---

## Key Features

### Identity and lifecycle

| Feature | Description |
|---|---|
| User CRUD | Create, read, update, enable, disable, and delete accounts in AD with optimistic concurrency via `whenChanged` and SQL `RowVersion` |
| OU picker | Admins create users only in OUs whitelisted in configuration — no arbitrary OU writes |
| Password reset | Random 16-character generator, LDAPS-only `unicodePwd` write, `pwdLastSet = 0` to force change at next login |
| Group membership | Type-ahead group picker, add/remove via `member` attribute on the group object, per-change audit rows |
| Soft delete | Move to "Deleted Users" OU with configurable grace period before hard delete |

### Intelligence

| Feature | Description |
|---|---|
| Stale risk scoring | ML.NET `FastTree` binary classifier over 8 features (days since login, contract type, group count, has-manager, etc.) |
| Nightly retrain | Windows Task Scheduler runs `UserMgmt.MLTrainer` at 02:00, writes model metrics to SQL for drift monitoring |
| Drift guard | New model rejected if AUC drops by more than 5% vs. previous run |
| GDPR opt-out | `ExcludeFromMLScoring` flag on `UserAttributes` removes a user from automated profiling |

### Bulk and export

| Feature | Description |
|---|---|
| Multi-select | Checkbox column, "select all" applies to filtered rows only |
| Bulk actions | Disable, enable, delete, change department, add to group — all with confirm dialogs |
| Export | Filtered CSV or Excel, audit log entry per export with the filter criteria captured |

### Platform

| Feature | Description |
|---|---|
| Razor Pages web UI | Windows Authentication (Kerberos) with Forms-auth fallback, antiforgery on all posts, CSP headers |
| SwiftUI iPad app | `NavigationSplitView`, `@Observable` view models, OAuth 2.0 PKCE via AD FS, Face ID unlock |
| Offline cache (iPad) | Core Data snapshot of the last user list, banner indicates staleness |
| Localisation | German (default) and English, `.resx` resources, locale-aware date formatting |
| Accessibility | WCAG 2.1 AA: focus trapping in modals, ARIA live regions, keyboard navigation end-to-end |

### Observability and compliance

| Feature | Description |
|---|---|
| Append-only audit log | Field-level old/new values, actor UPN, IP, source (Web/API/MLRetrain), DB-level DENY DELETE/UPDATE |
| Application log | Serilog with `MSSqlServer` sink, 90-day retention via SQL Agent |
| Health endpoint | `/health` reports AD bind, SQL reachability, ML model load status |
| Data subject reports | One-click export of everything the system holds about a given UPN |

---

## Tech Stack

| Category | Technology | Purpose |
|---|---|---|
| Web framework | ASP.NET Core 8 Razor Pages | Server-rendered admin UI, Windows Authentication, antiforgery |
| Web API | ASP.NET Core 8 Controllers + JWT Bearer | REST surface for the iPad client |
| Identity (directory) | Active Directory over LDAPS | Source of truth for identity; all password ops on port 636 |
| Identity (OAuth) | AD FS (on-premises) | OAuth 2.0 Authorization Code with PKCE for the iPad app |
| Persistence | SQL Server on-premises + EF Core | Sidecar store for custom attributes, audit, app logs, ML metrics |
| ORM | Entity Framework Core | `RowVersion` concurrency, interceptor-based query timing |
| Validation | FluentValidation | One validator set shared between Razor models and API DTOs |
| Logging | Serilog + `Serilog.Sinks.MSSqlServer` | Structured logs to SQL, 90-day retention |
| Machine learning | ML.NET 3.x (`BinaryClassification.FastTree`, AutoML) | Stale account prediction, served via `PredictionEnginePool` |
| Rate limiting | AspNetCoreRateLimit | 100 req/min per user on the API |
| iPad app | SwiftUI (iPadOS 17+) | Native two-column UI, `@Observable` state |
| iPad networking | `URLSession` async/await | Typed API client with Combine debounce on search |
| iPad auth | `ASWebAuthenticationSession` + `LAContext` | PKCE login, Face ID / Touch ID unlock |
| iPad storage | Keychain (biometric ACL) + Core Data | Refresh token and offline snapshot |
| Hosting | IIS with ANCM in-process | HTTPS-only, internal CA, gMSA application pool identity |
| Scheduler | Windows Task Scheduler | Nightly `UserMgmt.MLTrainer` runs |
| CI/CD | `dotnet publish` + Web Deploy, Xcode Cloud / Azure DevOps for iPad | Two parallel pipelines, one backend |

---

## Architecture

The system is a shared-service architecture. A single ASP.NET Core host on IIS terminates two client surfaces — Razor Pages (cookie auth) and JSON API (JWT Bearer). Both surfaces call the same service layer: `AdService`, `AttributeService`, `AuditService`, and `MlPredictor`. Active Directory is the system of record for identity. SQL Server is the sidecar — it stores the firm-specific attributes, the append-only audit log, the application log (Serilog), the ML model metrics, and a `PendingSync` retry queue for partial-write recovery.

The ML.NET pipeline is deliberately out-of-process. A separate console app, `UserMgmt.MLTrainer`, reads the `AuditLog` (disable/delete rows as positive examples, active accounts as negatives), trains a `FastTree` binary classifier, and writes a serialized `.zip` model to disk. The web host loads the model via `PredictionEnginePool` on startup and serves predictions inline (<5ms per user).

A standalone console utility, `UserMgmt.ADImport`, copies users, groups, and SQL sidecar rows from the production AD forest to a lab forest for demo and UAT use. It is read-only against production, refuses to run if its target matches the production domain, and re-maps the `manager` DN reference across forests.

```mermaid
graph TD
    subgraph Clients
        Browser[Browser<br/>Razor Pages]
        iPad[iPad App<br/>SwiftUI]
    end

    subgraph IIS["IIS Host (ANCM in-process)"]
        Razor[Razor Pages<br/>Cookie auth]
        API[API Controllers<br/>JWT Bearer]
    end

    subgraph Services["Service Layer (shared)"]
        ADSvc[AD Service<br/>LDAPS]
        AttrSvc[Attribute Service<br/>EF Core]
        AuditSvc[Audit Service]
        MLPred[ML.NET Predictor<br/>PredictionEnginePool]
    end

    subgraph Data
        AD[(Active Directory)]
        SQL[(SQL Server<br/>UserMgmt DB)]
        Model[(Model .zip<br/>on disk)]
    end

    subgraph Background
        Trainer[UserMgmt.MLTrainer<br/>Task Scheduler 02:00]
        Retry[PendingSync Worker<br/>Hosted Service]
    end

    Browser -->|Kerberos / Forms| Razor
    iPad -->|OAuth 2.0 PKCE| ADFS[AD FS]
    iPad -->|JWT Bearer| API
    ADFS -.-> AD

    Razor --> Services
    API --> Services

    ADSvc -->|LDAPS :636| AD
    AttrSvc --> SQL
    AuditSvc --> SQL
    MLPred --> Model

    Trainer -->|Read training data| SQL
    Trainer -->|Write model| Model
    Trainer -->|Metrics| SQL
    Retry --> SQL
    Retry --> ADSvc
```

---

## Code Structure

The solution is split into a handful of focused projects. The web host and API share a service layer; the ML trainer and AD import tool are independent console apps; the iPad app is its own Xcode workspace.

```text
ad-user-management/
├── src/
│   ├── UserMgmt.Web/                  ASP.NET Core host (Razor Pages + API)
│   │   ├── Pages/                     Razor Pages (Index, Edit, Groups, Audit)
│   │   ├── Api/                       Controllers for the iPad client
│   │   ├── wwwroot/                   Static assets, CSS, CSP-compatible JS
│   │   └── Program.cs                 DI, auth, Serilog, ML pool, health checks
│   ├── UserMgmt.Core/                 Domain + service layer (shared)
│   │   ├── Services/                  AdService, AttributeService, AuditService
│   │   ├── Ml/                        UserFeatures, StalePrediction, MlPredictor
│   │   ├── Validation/                FluentValidation validators
│   │   └── Ldap/                      LdapFilterEscape, LdapConnection pool
│   ├── UserMgmt.Data/                 EF Core DbContext + migrations
│   │   ├── Entities/                  UserAttributes, AuditLog, AppLog, PendingSync
│   │   └── Interceptors/              Query timing, audit interceptor
│   ├── UserMgmt.MLTrainer/            Console — nightly retrain job
│   ├── UserMgmt.ADImport/             Console — prod → dev forest import
│   └── UserMgmt.Website/              Marketing site (Razor Pages / static)
├── ipad/
│   └── UserMgmt.iPad/                 Xcode project (SwiftUI)
│       ├── Features/
│       │   ├── Login/                 LoginView, ASWebAuthenticationSession
│       │   ├── UserList/              UserListView, filter chips, search
│       │   ├── UserDetail/            Tabs: Identity, Org, Custom, Audit
│       │   └── Settings/
│       ├── Services/                  ApiClient, KeychainStore, CoreDataCache
│       └── Models/                    UserDto, StaleRiskLevel, Role
├── tests/
│   ├── UserMgmt.Core.Tests/           xUnit — services, validators, LDAP escape
│   ├── UserMgmt.Web.Tests/            Integration tests for Razor + API
│   ├── UserMgmt.MLTrainer.Tests/      Model training fixtures
│   └── UserMgmt.iPad.Tests/           Swift Testing — view models, API client
├── deploy/
│   ├── iis/                           web.config, ANCM settings
│   ├── sql/                           Schema, seed, retention SQL Agent jobs
│   └── ad/                            dsacls scripts for OU delegation
└── README.md
```

The class diagram below focuses on the server-side service layer. The iPad models are not repeated here — they are straightforward DTOs over the API.

```mermaid
classDiagram
    class IAdService {
        <<interface>>
        +SearchAsync(query, page) PagedResult~AdUser~
        +CreateAsync(dto, password) Task~AdUser~
        +UpdateAsync(upn, changes, ifMatch) Task
        +SetEnabledAsync(upn, enabled) Task
        +ResetPasswordAsync(upn, password) Task
        +AddToGroupAsync(upn, groupDn) Task
    }

    class IAttributeService {
        <<interface>>
        +GetAsync(upn) UserAttributes
        +UpsertAsync(upn, dto) Task
        +SetExcludeFromMlAsync(upn, bool) Task
    }

    class IAuditService {
        <<interface>>
        +RecordAsync(entry) Task
        +QueryForUserAsync(upn, page) PagedResult~AuditEntry~
    }

    class IMlPredictor {
        <<interface>>
        +Predict(features) StalePrediction
        +IsModelLoaded bool
    }

    class AdService {
        -LdapConnection connection
        -LdapFilterEscape escape
    }

    class AttributeService {
        -UserMgmtDbContext db
    }

    class AuditService {
        -UserMgmtDbContext db
    }

    class MlPredictor {
        -PredictionEnginePool~UserFeatures,StalePrediction~ pool
    }

    class UserAttributes {
        +string Upn
        +string EmployeeId
        +string CostCenter
        +string ContractType
        +float StaleRiskScore
        +bool ExcludeFromMLScoring
        +byte[] RowVersion
    }

    class AuditEntry {
        +long Id
        +DateTime Timestamp
        +string ActorUpn
        +string Action
        +string TargetUpn
        +string FieldName
        +string OldValue
        +string NewValue
        +string Source
    }

    class UserFeatures {
        +float DaysSinceLastLogin
        +float DaysSinceCreated
        +string ContractType
        +string Department
        +bool IsCurrentlyActive
        +float LoginFrequencyLast90Days
        +int GroupCount
        +bool HasManager
    }

    class StalePrediction {
        +bool PredictedLabel
        +float Probability
        +float Score
    }

    IAdService <|.. AdService
    IAttributeService <|.. AttributeService
    IAuditService <|.. AuditService
    IMlPredictor <|.. MlPredictor

    AttributeService --> UserAttributes
    AuditService --> AuditEntry
    MlPredictor --> UserFeatures
    MlPredictor --> StalePrediction
```

---

## Sequence Diagrams

### Create user (Razor Pages)

The create flow is the canonical "happy path" — it exercises AD validation, LDAPS password write, SQL sidecar insert, and the audit trail in a single request. A partial success (AD succeeded, SQL failed) is surfaced to the admin, not silently swallowed.

```mermaid
sequenceDiagram
    actor Admin
    participant Razor as Razor Page
    participant AdSvc as AdService
    participant AttrSvc as AttributeService
    participant Audit as AuditService
    participant AD as Active Directory
    participant SQL as SQL Server

    Admin->>Razor: Submit Create form (UPN, names, OU, password)
    Razor->>Razor: FluentValidation — fail fast on bad input
    Razor->>AdSvc: CreateAsync(dto, password)
    AdSvc->>AD: Search (uniqueness check on UPN, escaped)
    AD-->>AdSvc: Not found
    AdSvc->>AD: Add user object in chosen OU
    AdSvc->>AD: Set unicodePwd over LDAPS
    AdSvc->>AD: Set pwdLastSet = 0, clear ACCOUNTDISABLE
    AD-->>AdSvc: OK
    AdSvc-->>Razor: AdUser (sAMAccountName, UPN)

    Razor->>AttrSvc: UpsertAsync(UPN, customAttrs)
    AttrSvc->>SQL: INSERT UserAttributes
    alt SQL succeeds
        SQL-->>AttrSvc: OK
    else SQL fails
        SQL-->>AttrSvc: Error
        AttrSvc-->>Razor: Partial success (AD ok, SQL missing)
        Note over Razor: Warning banner, enqueue in PendingSync
    end

    Razor->>Audit: RecordAsync(Action=Create, Actor, Target=UPN)
    Audit->>SQL: INSERT AuditLog (password NEVER logged)
    Razor-->>Admin: Redirect to user detail with toast
```

### iPad login and stale-risk-gated user list

The iPad flow demonstrates the split between the identity provider (AD FS), the API, and the ML.NET predictor. Face ID unlocks the keychain-stored refresh token on subsequent launches so the user never sees the web view again unless the refresh token expires.

```mermaid
sequenceDiagram
    actor User
    participant App as iPad App
    participant Keychain as iOS Keychain
    participant ADFS as AD FS
    participant API as ASP.NET Core API
    participant AdSvc as AdService
    participant MLPred as MlPredictor
    participant AttrSvc as AttributeService

    User->>App: Launch
    App->>Keychain: Read refresh token (Face ID)
    alt Token present and valid
        Keychain-->>App: Refresh token
        App->>ADFS: POST /token (refresh)
        ADFS-->>App: New access token (1h)
    else No token
        App->>ADFS: ASWebAuthenticationSession → /authorize (PKCE)
        User->>ADFS: Sign in
        ADFS-->>App: Authorization code
        App->>ADFS: POST /token (code + verifier)
        ADFS-->>App: Access + refresh tokens
        App->>Keychain: Store refresh (biometric ACL)
    end

    App->>API: GET /users?page=1&pageSize=50 (Bearer)
    API->>API: Validate JWT, map role claims
    API->>AdSvc: SearchAsync(query)
    AdSvc-->>API: AD users
    API->>AttrSvc: GetManyAsync(UPNs)
    AttrSvc-->>API: Custom attributes + RowVersion

    loop For each user not excluded
        API->>MLPred: Predict(features)
        MLPred-->>API: StalePrediction (score)
    end

    API-->>App: PagedResult with merged users + risk
    App->>App: Cache to Core Data for offline
    App-->>User: Render NavigationSplitView
```

### Nightly ML retrain with drift guard

The trainer is out-of-process by design — a crash or long run cannot take the web host down. The drift guard protects the production serving path from a newly regressed model.

```mermaid
sequenceDiagram
    participant Scheduler as Task Scheduler
    participant Trainer as UserMgmt.MLTrainer
    participant SQL as SQL Server
    participant Disk as Model .zip on disk
    participant Web as IIS / Web Host

    Scheduler->>Trainer: Start (02:00 daily)
    Trainer->>SQL: SELECT AuditLog WHERE Action IN ('Disable','Delete')
    Trainer->>SQL: SELECT active users as negatives
    SQL-->>Trainer: Training set
    Trainer->>Trainer: FastTree + AutoML cross-validation
    Trainer->>SQL: SELECT previous AUC from MLModelMetrics
    SQL-->>Trainer: Previous AUC

    alt New AUC drop > 5%
        Trainer->>SQL: INSERT MLModelMetrics (status=Rejected)
        Trainer-->>Scheduler: Exit, keep old model
    else Acceptable
        Trainer->>Disk: Write model.zip (atomic replace)
        Trainer->>SQL: INSERT MLModelMetrics (AUC, F1, status=Accepted)
        Note over Web: Next prediction reloads pool lazily
    end
```

---

## Roadmap

This project is currently at **specification stage**. No production code has shipped. The roadmap below tracks the path from spec to v1 release.

| Milestone | Scope | Status |
|---|---|---|
| M0 — Specification | System design, data model, security, compliance mapping | Complete |
| M1 — Service layer | `AdService`, `AttributeService`, `AuditService` with unit tests | Planned |
| M2 — Razor web UI | User list, search, filters, edit dialog, OU picker | Planned |
| M3 — REST API | JWT Bearer, rate limiting, paginated endpoints | Planned |
| M4 — iPad app | SwiftUI split view, PKCE login, Face ID, offline cache | Planned |
| M5 — ML.NET | Trainer console app, prediction pool, drift guard | Planned |
| M6 — Compliance hardening | Append-only audit enforcement, DPIA sign-off, ISO 27001 evidence pack | Planned |
| M7 — AD import tool | Prod → dev forest import with dry-run and safety check | Planned |
| M8 — Product website | Marketing site, Impressum, demo form | Planned |

### Explicitly deferred (v2)

- User photo display and upload (`thumbnailPhoto` read/write).
- Anomalous login detection (second ML model, real-time event stream).
- Push notifications for high-risk accounts (APNs).
- Offline write queuing on the iPad.
- Full onboarding and offboarding workflows (chained account + mailbox + home directory + HR notify).

---

## Status

**Planned — specification approved, implementation not yet started.**

All architectural decisions, data schemas, security controls, and compliance mappings in this document are drawn from the approved v1.0 draft specification. The repository will be progressively populated through the milestones above; this README will be updated with screenshots, benchmarks, and build status badges as each milestone lands.

---

## License

Released under the [MIT License](./LICENSE).
