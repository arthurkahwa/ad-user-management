# AD export / import PowerShell scripts

Lightweight PowerShell precursor to the M7 `UserMgmt.ADImport` console
application. Two scripts that move user, group, attribute, and group
membership data from the `ap-architekten.local` production forest into
the `jab.loxal` development forest so engineers can run, test, and
demo the application against a realistic but disposable directory.

The scripts share a JSON document as their integration contract. The
export is read-only against production; the import is hard-pinned to
`jab.loxal` and refuses to run against any other forest.

---

## What the scripts do

### `Export-AdUsersAndGroups.ps1`

- Connects to a DC in `ap-architekten.local`.
- Asserts the bound forest is `ap-architekten.local` and aborts
  otherwise.
- Enumerates `Get-ADUser` and `Get-ADGroup` under an optional
  `-SearchBase` (default: the whole domain).
- Skips system accounts (`krbtgt`, `Guest`, `Administrator`,
  `DefaultAccount`, legacy `IUSR_*` / `IWAM_*` / `SQLServer*` /
  `MSOL_*` service accounts) and disabled accounts (unless
  `-IncludeDisabled` is set).
- Skips built-in groups (`Domain Admins`, `Enterprise Admins`,
  `Schema Admins`, `Administrators`, `Users`, `Guests`, `Replicator`)
  and anything under `CN=Builtin,...`.
- Resolves manager DNs and group member DNs to `SamAccountName`s at
  export time so the import never has to interpret a source-forest DN.
- Captures a curated set of attributes; never writes any
  password-related attribute (`UnicodePwd`, `LmPwdHistory`,
  `NtPwdHistory`, `dBCSPwd`, `SupplementalCredentials`,
  `msDS-KeyCredentialLink`) to the JSON.
- Writes a pretty-printed JSON document (schema version 1) to
  `-OutputPath` and a transcript log to
  `$env:TEMP\ad-export-<timestamp>.log`.

### `Import-AdUsersAndGroups.ps1`

- Connects to a DC in the target forest and verifies it is `jab.loxal`
  — and explicitly that it is not `ap-architekten.local`. Either
  condition aborts the run.
- Validates the JSON `schemaVersion`.
- Verifies the supplied `-TargetOu` exists (does not auto-create top
  level OUs).
- Creates intermediate OUs under `-TargetOu` to mirror the
  `relativeOuPath` values from the export.
- Three passes:
  1. **Groups.** Creates each group under `<TargetOu>/<relativeOuPath>`
     unless one with the same `SamAccountName` already exists in the
     import subtree.
  2. **Users.** Creates each user with a freshly generated
     cryptographically-random password, re-maps the UPN to the target
     forest (`jdoe@ap-architekten.local` → `jdoe@jab.loxal`), and
     forces `ChangePasswordAtLogon`. A second sub-pass re-maps the
     `manager` attribute by `SamAccountName` once every user is in
     place.
  3. **Memberships.** Adds users and nested groups to their group with
     `Add-ADGroupMember`, falling back to per-member add if the bulk
     call hits a pre-existing membership.
- Writes generated passwords to `<InputPath>.passwords.csv`,
  restricting the NTFS ACL to the current user.
- Writes a transcript log and a structured JSON event log to
  `$env:TEMP\ad-import-<timestamp>.{log,json}`.
- Supports `-WhatIf` and `-Confirm` for dry-runs.

---

## Prerequisites

Run from a domain-joined Windows workstation that has the
`ActiveDirectory` PowerShell module:

```powershell
# Windows Server (DC, member server, or admin jump-box):
Install-WindowsFeature RSAT-AD-PowerShell

# Windows 10 / 11 workstation:
Add-WindowsCapability -Online -Name Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0
```

PowerShell 5.1 or later. The scripts are also compatible with
PowerShell 7 on Windows.

The account that runs the export needs read access to user and group
objects in the source forest. The account that runs the import needs
permission to create users, groups, and OUs under `-TargetOu` in the
target forest.

---

## Worked example

Export the full source forest, then re-import into the dev forest:

```powershell
.\Export-AdUsersAndGroups.ps1 `
    -Server apsrv007vsn `
    -OutputPath C:\Temp\ap-export.json

.\Import-AdUsersAndGroups.ps1 `
    -InputPath C:\Temp\ap-export.json `
    -Server jab-dc01.jab.loxal `
    -TargetOu "OU=Imported,DC=jab,DC=loxal"
```

Scope a single OU and include disabled accounts (useful for round-trip
testing of the offboarding flow):

```powershell
.\Export-AdUsersAndGroups.ps1 `
    -Server apsrv007vsn `
    -SearchBase "OU=Architects,DC=ap-architekten,DC=local" `
    -OutputPath C:\Temp\ap-architects.json `
    -IncludeDisabled
```

Dry-run the import:

```powershell
.\Import-AdUsersAndGroups.ps1 `
    -InputPath C:\Temp\ap-export.json `
    -Server jab-dc01.jab.loxal `
    -TargetOu "OU=Imported,DC=jab,DC=loxal" `
    -WhatIf
```

---

## JSON schema (schemaVersion 1)

The two scripts negotiate through this document. The shape is stable
within a major version; additive changes go to schemaVersion 2.

```json
{
  "schemaVersion": 1,
  "exportedAtUtc": "2026-05-16T12:34:56Z",
  "sourceForest": "ap-architekten.local",
  "sourceDc": "apsrv007vsn",
  "searchBase": "DC=ap-architekten,DC=local",
  "userCount": 142,
  "groupCount": 38,
  "users": [
    {
      "samAccountName": "jdoe",
      "userPrincipalName": "jdoe@ap-architekten.local",
      "objectGuid": "...",
      "relativeOuPath": "OU=Architects,OU=Users",
      "attributes": {
        "givenName": "John",
        "sn": "Doe",
        "initials": null,
        "displayName": "John Doe",
        "personalTitle": "Dipl.-Ing.",
        "mail": "jdoe@ap-architekten.local",
        "telephoneNumber": "...",
        "mobile": null,
        "office": null,
        "physicalDeliveryOffice": "...",
        "department": "Architecture",
        "title": "Senior Architect",
        "company": "ap-architekten",
        "managerSamAccountName": "msmith",
        "accountEnabled": true,
        "accountExpirationDateUtc": null,
        "whenCreatedUtc": "2019-03-12T08:00:00Z",
        "description": null,
        "info": null,
        "employeeId": null,
        "employeeNumber": null,
        "employeeType": null
      }
    }
  ],
  "groups": [
    {
      "samAccountName": "Architects",
      "relativeOuPath": "OU=Groups",
      "description": "Architecture team",
      "groupCategory": "Security",
      "groupScope": "Global",
      "members": ["jdoe", "msmith", "group:SeniorArchitects"]
    }
  ]
}
```

Notes on shape:

- `relativeOuPath` is the OU portion of the DN with the domain DN
  suffix and the leaf `CN=` stripped. The import recreates the same
  OU hierarchy under `-TargetOu`.
- `managerSamAccountName` is a `SamAccountName`, not a DN. The import
  re-resolves it to the target-forest DN after both users exist.
- Group members that are themselves groups are prefixed with `group:`
  so the import can disambiguate during the membership pass.
- Cross-forest references (the source-forest UPN, the source-forest
  manager DN) are not used by the import directly — the UPN is
  re-mapped from `@ap-architekten.local` to `@jab.loxal`, and the
  manager is looked up by SAM.

---

## Safety guarantees

- **Read-only against the source.** The export script only ever issues
  `Get-AD*` cmdlets against `-Server`. There is no `New-AD*`,
  `Set-AD*`, or `Remove-AD*` anywhere in the export script.
- **Hard pin to `jab.loxal` for writes.** The import script aborts
  unless `(Get-ADDomain -Server $Server).DNSRoot -eq 'jab.loxal'` AND
  the DC is not in `ap-architekten.local`. Both conditions are checked
  explicitly.
- **No password data is exported.** A `$forbiddenProperties` array
  filters out `UnicodePwd`, `LmPwdHistory`, `NtPwdHistory`, `dBCSPwd`,
  `SupplementalCredentials`, and `msDS-KeyCredentialLink` even if the
  underlying `Get-ADUser` happens to surface them.
- **Generated passwords are dropped to a permission-locked CSV.** The
  import writes initial passwords to `<InputPath>.passwords.csv` and
  applies a restrictive ACL (current user + SYSTEM only).
- **Idempotent where possible.** The import skips groups, users, and
  OUs that already exist in the import subtree; rerunning the import
  after a partial failure does not create duplicates.
- **`-WhatIf` and `-Confirm` are respected** on every write in the
  import script (`SupportsShouldProcess = $true`).
- **Audit trail on every run.** Both scripts write a PowerShell
  transcript to `$env:TEMP\ad-<verb>-<timestamp>.log`. The import
  additionally writes a structured JSON event log next to the
  transcript so changes in the target forest can be machine-replayed.

---

## Limitations

These are intentional limits for the scripts as a precursor; the full
M7 `UserMgmt.ADImport` console application will revisit several of them.

- **Group Policy is not migrated.** GPO links, settings, and security
  filters live in SYSVOL and are forest-specific. Use `Backup-GPO` /
  `Import-GPO` separately if dev GPOs are needed.
- **Computer accounts are not migrated.** The scripts handle user and
  group objects only.
- **ACLs are not migrated.** Neither `ntSecurityDescriptor` nor any
  per-OU delegation is exported. The dev forest should use
  `deploy/ad/*.dsacls` scripts (when those land for M7) to apply its
  own delegation model.
- **`objectGuid` is not preserved.** AD generates a new GUID for the
  recreated object. The export captures the source GUID for traceability
  only; the import does not attempt to re-use it.
- **`userAccountControl` flags beyond the `ACCOUNTDISABLE` bit are
  dropped.** `PASSWORD_NOTREQD`, `SMARTCARD_REQUIRED`,
  `TRUSTED_FOR_DELEGATION`, `DONT_EXPIRE_PASSWORD`, etc. are not
  re-applied in the target. The recreated account is a baseline user
  with the source's `Enabled` bit and `ChangePasswordAtLogon = $true`.
- **Password history is not migrated.** Each imported user gets a
  freshly generated initial password.
- **The export's manager / member resolution is single-DC.** A member
  that has been deleted but not yet replicated to the chosen `-Server`
  will silently fall out of the export with a warning.
- **No partial-failure rollback.** If the import fails halfway through,
  the partial state remains in the target forest. Re-running is safe
  (idempotent), but the operator should review the transcript for any
  warnings before assuming the next run will complete cleanly.

---

## Where this fits in the roadmap

These two scripts are a lightweight precursor to milestone **M7** —
the `UserMgmt.ADImport` console application that will replace them.
The C# console will reuse the JSON schema documented here and will
add structured logging, retry policies, configurable filtering rules,
and integration with the existing service layer (`AdService`,
`AuditService`). Until M7 ships, these scripts are the supported way
to seed the dev forest.
