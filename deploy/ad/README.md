# AD export / import PowerShell scripts

Lightweight PowerShell precursor to the M7 `UserMgmt.ADImport` console
application. Two scripts that move user, group, attribute, and group
membership data from the `source-forest.local` production forest into
the `jab.loxal` development forest so engineers can run, test, and
demo the application against a realistic but disposable directory.

The scripts share a JSON document as their integration contract. The
export is read-only against production; the import is hard-pinned to
`jab.loxal` and refuses to run against any other forest.

---

## What the scripts do

### `Export-AdUsersAndGroups.ps1`

- Connects to a DC in `source-forest.local` over raw LDAP using
  `System.DirectoryServices.DirectoryEntry` /
  `System.DirectoryServices.DirectorySearcher`. The script does NOT
  require the `ActiveDirectory` PowerShell module (no RSAT) and does
  NOT require Active Directory Web Services (ADWS, port 9389) on the
  target DC. Only the LDAP port (389 cleartext by default, or 636
  over TLS when `-UseLdaps` is supplied) must be reachable.
- Asserts the bound DC's `rootDSE.defaultNamingContext` ends with
  `DC=source-forest,DC=local` and aborts otherwise.
- Enumerates user and group objects under an optional `-SearchBase`
  (default: the rootDSE's `defaultNamingContext`).
- Skips system accounts (`krbtgt`, `Guest`, `Administrator`,
  `DefaultAccount`, legacy `IUSR_*` / `IWAM_*` / `SQLServer*` /
  `MSOL_*` service accounts) and disabled accounts (unless
  `-IncludeDisabled` is set).
- Skips built-in groups (`Domain Admins`, `Enterprise Admins`,
  `Schema Admins`, `Administrators`, `Users`, `Guests`, `Replicator`)
  and anything under `CN=Builtin,...`.
- Resolves manager DNs and group member DNs to `sAMAccountName`s at
  export time so the import never has to interpret a source-forest DN.
- Captures a curated set of attributes; never writes any
  password-related attribute (`unicodePwd`, `lmPwdHistory`,
  `ntPwdHistory`, `dBCSPwd`, `supplementalCredentials`,
  `msDS-KeyCredentialLink`) to the JSON.
- Writes a pretty-printed JSON document (schema version 1) to
  `-OutputPath` and a transcript log to
  `$env:TEMP\ad-export-<timestamp>.log`.

### `Import-AdUsersAndGroups.ps1`

- Connects to a DC in the target forest and verifies it is `jab.loxal`
  — and explicitly that it is not `source-forest.local`. Either
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
     forest (`jdoe@source-forest.local` → `jdoe@jab.loxal`), and
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

PowerShell 5.1 or later on Windows. The scripts are also compatible
with PowerShell 7 on Windows.

The **export** script has no module prerequisites. It uses
`System.DirectoryServices.DirectoryEntry` and
`System.DirectoryServices.DirectorySearcher` (which ship with .NET
Framework on every Windows machine) to talk raw LDAP to the source
DC. No RSAT install, no ADWS dependency.

The **import** script still uses the `ActiveDirectory` PowerShell
module (`RSAT-AD-PowerShell`) because it needs `New-ADUser`,
`New-ADGroup`, etc. against the target forest:

```powershell
# Windows Server (DC, member server, or admin jump-box):
Install-WindowsFeature RSAT-AD-PowerShell

# Windows 10 / 11 workstation:
Add-WindowsCapability -Online -Name Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0
```

The account that runs the export needs read access to user and group
objects in the source forest. The account that runs the import needs
permission to create users, groups, and OUs under `-TargetOu` in the
target forest.

---

## Worked example

Export the full source forest, then re-import into the dev forest:

```powershell
.\Export-AdUsersAndGroups.ps1 `
    -Server dc01 `
    -OutputPath C:\Temp\export.json

.\Import-AdUsersAndGroups.ps1 `
    -InputPath C:\Temp\export.json `
    -Server jab-dc01.jab.loxal `
    -TargetOu "OU=Imported,DC=jab,DC=loxal"
```

Scope a single OU and include disabled accounts (useful for round-trip
testing of the offboarding flow):

```powershell
.\Export-AdUsersAndGroups.ps1 `
    -Server dc01 `
    -SearchBase "OU=Engineering,DC=source-forest,DC=local" `
    -OutputPath C:\Temp\engineering.json `
    -IncludeDisabled
```

In environments where cleartext LDAP (port 389) is blocked by policy,
add `-UseLdaps` so the export binds on port 636 over TLS instead:

```powershell
.\Export-AdUsersAndGroups.ps1 `
    -Server dc01 `
    -OutputPath C:\Temp\export.json `
    -UseLdaps
```

Dry-run the import:

```powershell
.\Import-AdUsersAndGroups.ps1 `
    -InputPath C:\Temp\export.json `
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
  "sourceForest": "source-forest.local",
  "sourceDc": "dc01",
  "searchBase": "DC=source-forest,DC=local",
  "userCount": 142,
  "groupCount": 38,
  "users": [
    {
      "samAccountName": "jdoe",
      "userPrincipalName": "jdoe@source-forest.local",
      "objectGuid": "...",
      "relativeOuPath": "OU=Engineering,OU=Users",
      "attributes": {
        "givenName": "John",
        "sn": "Doe",
        "initials": null,
        "displayName": "John Doe",
        "personalTitle": "Dipl.-Ing.",
        "mail": "jdoe@source-forest.local",
        "telephoneNumber": "...",
        "mobile": null,
        "office": null,
        "physicalDeliveryOffice": "...",
        "department": "Engineering",
        "title": "Senior Engineer",
        "company": "source-forest",
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
      "samAccountName": "Engineers",
      "relativeOuPath": "OU=Groups",
      "description": "Engineering team",
      "groupCategory": "Security",
      "groupScope": "Global",
      "members": ["jdoe", "msmith", "group:SeniorEngineers"]
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
  re-mapped from `@source-forest.local` to `@jab.loxal`, and the
  manager is looked up by SAM.

---

## Safety guarantees

- **Read-only against the source.** The export script only ever
  issues LDAP search operations (`DirectorySearcher.FindAll` /
  `FindOne`) against `-Server`. There are no
  `DirectoryEntry.CommitChanges()`, `Add`, `Put`, or any other write
  operations anywhere in the export script.
- **Hard pin to `jab.loxal` for writes.** The import script aborts
  unless `(Get-ADDomain -Server $Server).DNSRoot -eq 'jab.loxal'` AND
  the DC is not in `source-forest.local`. Both conditions are checked
  explicitly.
- **No password data is exported.** The script requests a curated
  `PropertiesToLoad` list and never asks AD for credential
  attributes. A `$forbiddenProperties` assertion just before
  serialisation catches any future edit that accidentally adds
  `unicodePwd`, `lmPwdHistory`, `ntPwdHistory`, `dBCSPwd`,
  `supplementalCredentials`, or `msDS-KeyCredentialLink` to the
  emitted attribute hashtable.
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
