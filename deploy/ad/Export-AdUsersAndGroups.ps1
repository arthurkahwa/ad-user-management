<#
.SYNOPSIS
    Exports users, groups, attributes, and group memberships from the
    `ap-architekten.local` production AD forest to a portable JSON file.

.DESCRIPTION
    Read-only against the source forest. Connects to the supplied DC,
    enumerates user and group objects under the (optional) search base,
    skips system / built-in accounts and disabled accounts (unless asked
    to include them), and emits a JSON document conforming to schema
    version 1 (see deploy/ad/README.md).

    The JSON is the integration contract with Import-AdUsersAndGroups.ps1.
    Designed for the M7 precursor flow: prod -> dev forest copy with
    safety checks. Never exports password-related attributes.

.PARAMETER Server
    Hostname of a domain controller in the source forest (e.g.
    `apsrv007vsn`). The script asserts the DC belongs to
    `ap-architekten.local` before reading anything; it refuses to run
    against any other forest. Mandatory.

.PARAMETER SearchBase
    Distinguished name of the OU subtree to export. Omit (default $null)
    to enumerate the entire domain. Lets the operator scope a single OU
    for testing.

.PARAMETER OutputPath
    Filesystem path the JSON document is written to. Existing files are
    overwritten. Mandatory.

.PARAMETER IncludeDisabled
    Switch. When set, disabled user accounts are included in the export.
    Default behaviour skips them — dev forests don't need ballast.

.PARAMETER Credential
    PSCredential bound when contacting the source DC. Omit to use the
    current Windows identity (typical when run from a domain-joined
    workstation).

.EXAMPLE
    .\Export-AdUsersAndGroups.ps1 -Server apsrv007vsn `
        -OutputPath C:\Temp\ap-export.json

    Export every active user and non-built-in group from the entire
    `ap-architekten.local` domain.

.EXAMPLE
    .\Export-AdUsersAndGroups.ps1 -Server apsrv007vsn `
        -SearchBase 'OU=Architects,DC=ap-architekten,DC=local' `
        -OutputPath C:\Temp\ap-architects.json -IncludeDisabled

    Export a single OU including disabled accounts, for round-trip
    testing of the import script.

.NOTES
    Requires the `ActiveDirectory` PowerShell module (RSAT-AD-PowerShell).
    PowerShell 5.1+ compatible (some customer DCs still run Windows
    Server 2019). Read-only against the source domain.

    A transcript log is written to
    `$env:TEMP\ad-export-<timestamp>.log` for the audit trail.

    Password-related attributes are NEVER captured, even if the
    underlying `Get-ADUser -Properties *` happens to surface them.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Server,

    [Parameter(Mandatory = $false)]
    [string] $SearchBase = $null,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $false)]
    [switch] $IncludeDisabled,

    [Parameter(Mandatory = $false)]
    [System.Management.Automation.PSCredential] $Credential
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WHY: scripts must surface unexpected errors loudly; silent partial
# exports would poison the import. Strict mode also catches typos in
# property names introduced by future refactors.

# WHY: a transcript captures every host call (Write-Host, Write-Verbose,
# error output) so a post-mortem after a failed run can reconstruct
# exactly what AD returned. The file lands in $env:TEMP rather than the
# repo so it doesn't accidentally get committed.
$timestamp     = (Get-Date -Format 'yyyyMMdd-HHmmss')
$transcriptLog = Join-Path -Path $env:TEMP -ChildPath ("ad-export-{0}.log" -f $timestamp)
Start-Transcript -Path $transcriptLog -Force | Out-Null

try {
    Import-Module ActiveDirectory -ErrorAction Stop

    # -------------------------------------------------------------------
    # Filtering tables — easy to amend without touching the body.
    # -------------------------------------------------------------------

    # WHY: pattern-based skip list for system / service accounts. The
    # default RID-500 (`Administrator`), `krbtgt`, `Guest`,
    # `DefaultAccount`, and the IIS / SQL service-account legacy
    # prefixes are excluded — they belong to the directory itself, not
    # to the human staff the dev forest needs.
    $systemAccountPattern = '^(krbtgt|Guest|Administrator|DefaultAccount)$|^IUSR_|^IWAM_|^SQLServer|^MSOL_'

    # WHY: built-in groups are forest-managed and cannot be safely
    # recreated in another forest by name. Skip anything that lives
    # under `CN=Builtin,...` plus the well-known privileged groups by
    # SamAccountName.
    $builtInGroupPattern = '^(Domain Admins|Enterprise Admins|Schema Admins|Administrators|Users|Guests|Replicator)$'

    # WHY: belt-and-braces against `-Properties *` returning credential
    # material. We never want password hashes leaving the source forest,
    # not even in memory after the export. The export emits the curated
    # set declared below, but this list is a second line of defence in
    # case future edits accidentally include `-Properties *`.
    $forbiddenProperties = @(
        'UnicodePwd', 'LmPwdHistory', 'NtPwdHistory', 'dBCSPwd',
        'SupplementalCredentials', 'msDS-KeyCredentialLink',
        'lmPwdHistory', 'ntPwdHistory'
    )

    # -------------------------------------------------------------------
    # Forest safety check — refuse to read anything until we have proved
    # the DC belongs to the expected source forest.
    # -------------------------------------------------------------------
    Write-Host "Verifying source forest..."
    $adParams = @{ Server = $Server }
    if ($Credential) { $adParams['Credential'] = $Credential }

    $sourceDomain = Get-ADDomain @adParams
    if ($sourceDomain.DNSRoot -ne 'ap-architekten.local') {
        throw "Refusing to export from '$($sourceDomain.DNSRoot)' — this script only reads from ap-architekten.local"
    }
    Write-Host ("Connected to {0} via DC {1}" -f $sourceDomain.DNSRoot, $Server)

    # Domain DN suffix used to strip the relative OU path from each
    # DistinguishedName. Stored verbatim in the JSON so the import can
    # decide whether to preserve OU hierarchy.
    $domainDn = $sourceDomain.DistinguishedName

    # -------------------------------------------------------------------
    # Curated user property list — never `-Properties *`.
    # -------------------------------------------------------------------
    $userProperties = @(
        'SamAccountName', 'UserPrincipalName', 'DistinguishedName',
        'ObjectGUID', 'SID',
        'GivenName', 'Surname', 'Initials', 'DisplayName', 'PersonalTitle',
        'Mail', 'TelephoneNumber', 'Mobile', 'Office',
        'physicalDeliveryOfficeName',
        'Department', 'Company', 'Title', 'Manager',
        'Enabled', 'AccountExpirationDate', 'WhenCreated', 'LastLogonDate',
        'Description', 'Info',
        'EmployeeID', 'EmployeeNumber', 'EmployeeType'
    )

    # -------------------------------------------------------------------
    # Enumerate users.
    # -------------------------------------------------------------------
    Write-Host "Enumerating users..."
    $userQuery = @{
        Filter     = '*'
        Server     = $Server
        Properties = $userProperties
    }
    if ($Credential)             { $userQuery['Credential'] = $Credential }
    if ($SearchBase)             { $userQuery['SearchBase'] = $SearchBase }

    $allUsers      = Get-ADUser @userQuery
    $exportedUsers = New-Object System.Collections.Generic.List[object]
    $skippedSystem = 0
    $skippedDisabled = 0

    foreach ($u in $allUsers) {
        if ($u.SamAccountName -match $systemAccountPattern) {
            $skippedSystem++
            continue
        }
        if (-not $IncludeDisabled.IsPresent -and -not $u.Enabled) {
            $skippedDisabled++
            continue
        }

        # WHY: resolve the manager DN to a SamAccountName during export so
        # the import can find the manager in the target forest without
        # depending on source-forest DNs (which won't exist in the dev
        # forest).
        $managerSam = $null
        if ($u.Manager) {
            try {
                $mgrParams = @{ Identity = $u.Manager; Server = $Server; ErrorAction = 'Stop' }
                if ($Credential) { $mgrParams['Credential'] = $Credential }
                $mgr = Get-ADUser @mgrParams
                $managerSam = $mgr.SamAccountName
            }
            catch {
                Write-Warning ("Manager DN '{0}' on user '{1}' could not be resolved; managerSamAccountName will be null." -f $u.Manager, $u.SamAccountName)
            }
        }

        # Strip the domain DN suffix from DistinguishedName to leave only
        # the relative OU path (e.g. "OU=Architects,OU=Users"). The leaf
        # CN is dropped because the import recreates it from
        # SamAccountName.
        $relativeOuPath = $null
        if ($u.DistinguishedName) {
            $dn = $u.DistinguishedName
            # Drop the leading CN=...,
            $withoutLeaf = $dn -replace '^CN=[^,]+,', ''
            # Drop the trailing domain DN suffix
            $suffix = ',' + $domainDn
            if ($withoutLeaf.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $relativeOuPath = $withoutLeaf.Substring(0, $withoutLeaf.Length - $suffix.Length)
            }
            else {
                $relativeOuPath = $withoutLeaf
            }
        }

        $accountExpires = $null
        if ($u.AccountExpirationDate) {
            $accountExpires = $u.AccountExpirationDate.ToUniversalTime().ToString('o')
        }
        $whenCreated = $null
        if ($u.WhenCreated) {
            $whenCreated = $u.WhenCreated.ToUniversalTime().ToString('o')
        }

        $entry = [ordered]@{
            samAccountName     = $u.SamAccountName
            userPrincipalName  = $u.UserPrincipalName
            objectGuid         = if ($u.ObjectGUID) { $u.ObjectGUID.ToString() } else { $null }
            relativeOuPath     = $relativeOuPath
            attributes         = [ordered]@{
                givenName                = $u.GivenName
                sn                       = $u.Surname
                initials                 = $u.Initials
                displayName              = $u.DisplayName
                personalTitle            = $u.PersonalTitle
                mail                     = $u.Mail
                telephoneNumber          = $u.TelephoneNumber
                mobile                   = $u.Mobile
                office                   = $u.Office
                physicalDeliveryOffice   = $u.physicalDeliveryOfficeName
                department               = $u.Department
                title                    = $u.Title
                company                  = $u.Company
                managerSamAccountName    = $managerSam
                accountEnabled           = [bool]$u.Enabled
                accountExpirationDateUtc = $accountExpires
                whenCreatedUtc           = $whenCreated
                description              = $u.Description
                info                     = $u.Info
                employeeId               = $u.EmployeeID
                employeeNumber           = $u.EmployeeNumber
                employeeType             = $u.EmployeeType
            }
        }

        # WHY: defence-in-depth — strip any forbidden property that may
        # have slipped through. Belt and braces against future edits.
        foreach ($forbidden in $forbiddenProperties) {
            if ($entry.attributes.Contains($forbidden)) {
                [void]$entry.attributes.Remove($forbidden)
            }
        }

        [void]$exportedUsers.Add([pscustomobject]$entry)
    }

    Write-Host ("Users: {0} retained, {1} system, {2} disabled" -f $exportedUsers.Count, $skippedSystem, $skippedDisabled)

    # -------------------------------------------------------------------
    # Enumerate groups.
    # -------------------------------------------------------------------
    Write-Host "Enumerating groups..."
    $groupQuery = @{
        Filter     = '*'
        Server     = $Server
        Properties = @('Description', 'GroupCategory', 'GroupScope', 'Members', 'DistinguishedName')
    }
    if ($Credential)             { $groupQuery['Credential'] = $Credential }
    if ($SearchBase)             { $groupQuery['SearchBase'] = $SearchBase }

    $allGroups      = Get-ADGroup @groupQuery
    $exportedGroups = New-Object System.Collections.Generic.List[object]
    $skippedBuiltIn = 0
    $totalMemberships = 0

    foreach ($g in $allGroups) {
        if ($g.SamAccountName -match $builtInGroupPattern) {
            $skippedBuiltIn++
            continue
        }
        # WHY: anything under CN=Builtin is a forest-managed group and
        # must not be recreated in the target.
        if ($g.DistinguishedName -match ',CN=Builtin,') {
            $skippedBuiltIn++
            continue
        }

        $relativeOuPath = $null
        if ($g.DistinguishedName) {
            $dn = $g.DistinguishedName
            $withoutLeaf = $dn -replace '^CN=[^,]+,', ''
            $suffix = ',' + $domainDn
            if ($withoutLeaf.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $relativeOuPath = $withoutLeaf.Substring(0, $withoutLeaf.Length - $suffix.Length)
            }
            else {
                $relativeOuPath = $withoutLeaf
            }
        }

        # WHY: resolve every member DN to a SamAccountName at export
        # time. The import doesn't have to (and shouldn't) trust
        # source-forest DNs. If the member is itself a group, prefix
        # with `group:` so the import can disambiguate during the
        # membership pass.
        $memberNames = New-Object System.Collections.Generic.List[string]
        if ($g.Members) {
            foreach ($memberDn in $g.Members) {
                try {
                    $memParams = @{
                        Identity    = $memberDn
                        Server      = $Server
                        Properties  = 'SamAccountName', 'objectClass'
                        ErrorAction = 'Stop'
                    }
                    if ($Credential) { $memParams['Credential'] = $Credential }
                    $obj = Get-ADObject @memParams
                    if (-not $obj.SamAccountName) {
                        Write-Warning ("Member '{0}' of group '{1}' has no SamAccountName; skipped." -f $memberDn, $g.SamAccountName)
                        continue
                    }
                    if ($obj.objectClass -contains 'group') {
                        [void]$memberNames.Add('group:' + $obj.SamAccountName)
                    }
                    else {
                        [void]$memberNames.Add($obj.SamAccountName)
                    }
                }
                catch {
                    Write-Warning ("Member DN '{0}' of group '{1}' could not be resolved: {2}" -f $memberDn, $g.SamAccountName, $_.Exception.Message)
                }
            }
        }

        $totalMemberships += $memberNames.Count

        $entry = [ordered]@{
            samAccountName = $g.SamAccountName
            relativeOuPath = $relativeOuPath
            description    = $g.Description
            groupCategory  = "$($g.GroupCategory)"
            groupScope     = "$($g.GroupScope)"
            members        = @($memberNames)
        }

        [void]$exportedGroups.Add([pscustomobject]$entry)
    }

    Write-Host ("Groups: {0} retained, {1} built-in" -f $exportedGroups.Count, $skippedBuiltIn)
    Write-Host ("Memberships: {0}" -f $totalMemberships)

    # -------------------------------------------------------------------
    # Compose the JSON document.
    # -------------------------------------------------------------------
    $document = [ordered]@{
        schemaVersion  = 1
        exportedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
        sourceForest   = $sourceDomain.DNSRoot
        sourceDc       = $Server
        searchBase     = if ($SearchBase) { $SearchBase } else { $domainDn }
        userCount      = $exportedUsers.Count
        groupCount     = $exportedGroups.Count
        users          = @($exportedUsers)
        groups         = @($exportedGroups)
    }

    # WHY: -Depth 8 covers users/attributes (2 levels) and groups/members
    # (1 level) with margin. Pretty-printed for easy human review of the
    # contract on disk before running the import.
    $json = $document | ConvertTo-Json -Depth 8
    Set-Content -Path $OutputPath -Value $json -Encoding UTF8 -Force

    $fileInfo = Get-Item -Path $OutputPath
    Write-Host ""
    Write-Host "Export complete."
    Write-Host ("  Output:        {0}" -f $fileInfo.FullName)
    Write-Host ("  Size:          {0:N0} bytes" -f $fileInfo.Length)
    Write-Host ("  Users:         {0}" -f $exportedUsers.Count)
    Write-Host ("  Groups:        {0}" -f $exportedGroups.Count)
    Write-Host ("  Memberships:   {0}" -f $totalMemberships)
    Write-Host ("  Transcript:    {0}" -f $transcriptLog)
}
finally {
    Stop-Transcript | Out-Null
}
