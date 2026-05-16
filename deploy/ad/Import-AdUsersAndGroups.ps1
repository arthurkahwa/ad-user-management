<#
.SYNOPSIS
    Imports the JSON document produced by Export-AdUsersAndGroups.ps1
    into the `jab.loxal` development AD forest.

.DESCRIPTION
    Idempotent (where possible) recreation of users, groups, OUs, and
    group memberships in a development forest. The script refuses to
    write to anything other than `jab.loxal` - the safety check at the
    top is the most important code in the file.

    Three passes:
      1. Groups   - created under TargetOu / relativeOuPath.
      2. Users    - created with a freshly generated random password.
                    Manager attribute is re-mapped from
                    `managerSamAccountName` to the new manager DN in the
                    target forest. UPN is re-mapped to the target
                    forest's DNS root.
      3. Memberships - Add-ADGroupMember per group, skipping any member
                    that wasn't created (with a warning).

    All generated passwords are written to a sibling CSV with
    restrictive ACLs so the operator can hand them out for first login.
    The CSV is the only artefact that contains credential material.

.PARAMETER InputPath
    Path to the JSON document produced by Export-AdUsersAndGroups.ps1.
    Mandatory.

.PARAMETER Server
    Hostname of a domain controller in the target forest (e.g.
    `jab-dc01.jab.loxal`). The script asserts the DC belongs to
    `jab.loxal` before writing anything. Mandatory.

.PARAMETER TargetOu
    Distinguished name of the base OU under which all imported users
    and groups will be created. Relative OU paths from the export are
    appended to this base. Mandatory.

.PARAMETER InitialPasswordLength
    Length of the random password generated for each new user.
    Defaults to 16. Must be at least 12 to keep the generated
    password compliant with typical complexity policies.

.PARAMETER Credential
    PSCredential bound when contacting the target DC. Omit to use the
    current Windows identity.

.EXAMPLE
    .\Import-AdUsersAndGroups.ps1 -InputPath C:\Temp\ap-export.json `
        -Server jab-dc01.jab.loxal `
        -TargetOu 'OU=Imported,DC=jab,DC=loxal'

    Import every user and group from the export into the `Imported`
    OU of the `jab.loxal` development forest.

.EXAMPLE
    .\Import-AdUsersAndGroups.ps1 -InputPath C:\Temp\ap-export.json `
        -Server jab-dc01.jab.loxal `
        -TargetOu 'OU=Imported,DC=jab,DC=loxal' -WhatIf

    Dry-run. Lists every write that would happen without changing the
    target directory.

.NOTES
    Requires the `ActiveDirectory` PowerShell module (RSAT-AD-PowerShell).
    PowerShell 5.1+ compatible.

    Two log artefacts are produced for every run:
      - $env:TEMP\ad-import-<timestamp>.log    transcript (human)
      - $env:TEMP\ad-import-<timestamp>.json   structured event log

    Generated passwords are written to <InputPath>.passwords.csv with
    ACLs restricting read access to the current user. Treat that file
    as a credential.
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
# WHY: ConvertTo-SecureString -AsPlainText is unavoidable here - New-ADUser
# requires a SecureString, and the password is generated locally from a
# CSPRNG (see New-RandomPassword). There is no plaintext source on disk
# being read in; the generated string is wrapped immediately and the
# plaintext is only written to a permission-locked CSV for the operator.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSAvoidUsingConvertToSecureStringWithPlainText', '',
    Justification = 'Locally generated CSPRNG output; required by New-ADUser; plaintext written only to ACL-locked CSV.')]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [Parameter(Mandatory = $true)]
    [string] $Server,

    [Parameter(Mandatory = $true)]
    [string] $TargetOu,

    [Parameter(Mandatory = $false)]
    [ValidateRange(12, 128)]
    [int] $InitialPasswordLength = 16,

    [Parameter(Mandatory = $false)]
    [System.Management.Automation.PSCredential] $Credential
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WHY: PS5.1+ does not have Get-Random with a built-in cryptographic
# source. The default System.Random is fine for non-credential use, but
# initial passwords for new accounts deserve a CSPRNG. RNGCryptoServiceProvider
# is available on every supported Windows host.
#
# PSScriptAnalyzer suppression: this function is a generator, not a state
# changer - the password it returns is bound into a SecureString by the
# caller and never persisted in plaintext outside the password CSV (which
# has a locked-down ACL applied immediately after write).
function New-RandomPassword {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute(
        'PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Pure generator; no state change.')]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(12, 128)]
        [int] $Length
    )

    $upper   = 'ABCDEFGHJKLMNPQRSTUVWXYZ'      # excluded I, O
    $lower   = 'abcdefghijkmnopqrstuvwxyz'     # excluded l
    $digit   = '23456789'                      # excluded 0, 1
    $symbol  = '!@#$%^&*()-_=+[]{};:,.?'

    $alphabet = ($upper + $lower + $digit + $symbol).ToCharArray()

    $rng   = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()
    $bytes = [byte[]]::new($Length)
    $rng.GetBytes($bytes)

    $chars = [char[]]::new($Length)
    for ($i = 0; $i -lt $Length; $i++) {
        $chars[$i] = $alphabet[$bytes[$i] % $alphabet.Length]
    }

    # WHY: guarantee at least one of each class so AD's default
    # complexity policy never rejects the generated password.
    $chars[0] = $upper[ ($bytes[0] % $upper.Length) ]
    $chars[1] = $lower[ ($bytes[1] % $lower.Length) ]
    $chars[2] = $digit[ ($bytes[2] % $digit.Length) ]
    $chars[3] = $symbol[ ($bytes[3] % $symbol.Length) ]

    -join $chars
}

# WHY: a small structured log file alongside the transcript lets
# operators feed the run into change-management tooling without
# regex-parsing the transcript.
$timestamp     = (Get-Date -Format 'yyyyMMdd-HHmmss')
$transcriptLog = Join-Path -Path $env:TEMP -ChildPath ("ad-import-{0}.log"  -f $timestamp)
$jsonEventLog  = Join-Path -Path $env:TEMP -ChildPath ("ad-import-{0}.json" -f $timestamp)
$eventBuffer   = New-Object System.Collections.Generic.List[object]

function Add-ImportEvent {
    param(
        [Parameter(Mandatory = $true)] [string] $Action,
        [Parameter(Mandatory = $true)] [string] $Subject,
        [Parameter(Mandatory = $true)] [string] $Outcome,
        [Parameter(Mandatory = $false)] [string] $Detail = $null
    )
    $eventBuffer.Add([pscustomobject]@{
        ts      = (Get-Date).ToUniversalTime().ToString('o')
        action  = $Action
        subject = $Subject
        outcome = $Outcome
        detail  = $Detail
    }) | Out-Null
}

Start-Transcript -Path $transcriptLog -Force | Out-Null

try {
    Import-Module ActiveDirectory -ErrorAction Stop

    # -------------------------------------------------------------------
    # Hard safety check - bound up front, before reading anything.
    # WHY: this is the only thing standing between a misconfigured
    # invocation and a production-forest write. Using BOTH conditions
    # defensively: an explicit deny-list (must not be the source forest)
    # AND an explicit allow-list (must be the dev forest). Either alone
    # could be defeated by a typo or DNS surprise; together they fail
    # closed.
    # -------------------------------------------------------------------
    Write-Host "Verifying target forest..."
    $adParams = @{ Server = $Server }
    if ($Credential) { $adParams['Credential'] = $Credential }

    $targetDomainObj = Get-ADDomain @adParams
    $targetDomain    = $targetDomainObj.DNSRoot

    if ($targetDomain -eq 'ap-architekten.local' -or $targetDomain -ne 'jab.loxal') {
        throw "Refusing to write to '$targetDomain' - this script only writes to jab.loxal"
    }
    Write-Host ("Connected to {0} via DC {1}" -f $targetDomain, $Server)

    # -------------------------------------------------------------------
    # Read and validate the JSON contract.
    # -------------------------------------------------------------------
    if (-not (Test-Path -Path $InputPath -PathType Leaf)) {
        throw "Input file not found: $InputPath"
    }
    Write-Host ("Reading export from {0}" -f $InputPath)
    $document = Get-Content -Path $InputPath -Raw -Encoding UTF8 | ConvertFrom-Json

    if ($document.schemaVersion -ne 1) {
        throw "Unsupported schemaVersion '$($document.schemaVersion)'. This import understands schemaVersion 1 only."
    }
    Write-Host ("Source forest: {0}, exported at: {1}" -f $document.sourceForest, $document.exportedAtUtc)
    Write-Host ("Document contains {0} users and {1} groups" -f $document.userCount, $document.groupCount)

    # -------------------------------------------------------------------
    # Ensure the target OU exists. We do NOT auto-create the base OU.
    # Creating top-level OUs is an operator decision and must be made
    # explicitly, never as a side-effect of running this script.
    # -------------------------------------------------------------------
    try {
        $ouCheckParams = @{ Identity = $TargetOu; Server = $Server; ErrorAction = 'Stop' }
        if ($Credential) { $ouCheckParams['Credential'] = $Credential }
        $null = Get-ADOrganizationalUnit @ouCheckParams
    }
    catch {
        throw "TargetOu '$TargetOu' does not exist in '$targetDomain'. Create it with `New-ADOrganizationalUnit` first - this script will not auto-create the base OU."
    }

    # -------------------------------------------------------------------
    # Helper: ensure all the intermediate OUs in a relative path exist
    # under TargetOu. Idempotent - existing OUs are left alone.
    # -------------------------------------------------------------------
    function Initialize-OuPath {
        [CmdletBinding(SupportsShouldProcess = $true)]
        param(
            [Parameter(Mandatory = $false)] [string] $RelativeOuPath
        )

        if ([string]::IsNullOrWhiteSpace($RelativeOuPath)) {
            return $TargetOu
        }

        # The relative path is parsed right-to-left so we can create
        # the parent OUs before their children. Example input:
        #   "OU=Architects,OU=Users"
        # We want to create OU=Users under TargetOu, then OU=Architects
        # under that.
        $segments = @()
        foreach ($segment in ($RelativeOuPath -split ',(?=OU=)')) {
            $trimmed = $segment.Trim()
            if ($trimmed) { $segments += $trimmed }
        }
        # Reverse so we process outermost (rightmost) first.
        [array]::Reverse($segments)

        $currentParent = $TargetOu
        foreach ($seg in $segments) {
            if (-not ($seg -match '^OU=(.+)$')) {
                Write-Warning ("Skipping non-OU segment '{0}' in relativeOuPath '{1}'" -f $seg, $RelativeOuPath)
                continue
            }
            $name        = $Matches[1]
            $candidateDn = "$seg,$currentParent"

            $exists = $false
            try {
                $checkParams = @{ Identity = $candidateDn; Server = $Server; ErrorAction = 'Stop' }
                if ($Credential) { $checkParams['Credential'] = $Credential }
                $null = Get-ADOrganizationalUnit @checkParams
                $exists = $true
            }
            catch {
                $exists = $false
            }

            if (-not $exists) {
                if ($PSCmdlet.ShouldProcess($candidateDn, 'Create OU')) {
                    $createParams = @{
                        Name                            = $name
                        Path                            = $currentParent
                        Server                          = $Server
                        ProtectedFromAccidentalDeletion = $false
                    }
                    if ($Credential) { $createParams['Credential'] = $Credential }
                    New-ADOrganizationalUnit @createParams
                    Add-ImportEvent -Action 'CreateOU' -Subject $candidateDn -Outcome 'Created'
                }
            }
            $currentParent = $candidateDn
        }
        return $currentParent
    }

    # -------------------------------------------------------------------
    # Helper: does an AD object (user or group) with this SamAccountName
    # exist anywhere under TargetOu? We deliberately scope the lookup
    # to the import sub-tree to avoid clobbering pre-existing dev users
    # that happen to share a name.
    # -------------------------------------------------------------------
    function Find-ExistingByName {
        param(
            [Parameter(Mandatory = $true)] [string] $SamAccountName,
            [Parameter(Mandatory = $true)] [ValidateSet('user','group')] [string] $Kind
        )

        $params = @{
            Filter      = ("SamAccountName -eq '{0}'" -f $SamAccountName.Replace("'", "''"))
            Server      = $Server
            SearchBase  = $TargetOu
            ErrorAction = 'SilentlyContinue'
        }
        if ($Credential) { $params['Credential'] = $Credential }

        if ($Kind -eq 'user') {
            return Get-ADUser @params
        }
        else {
            return Get-ADGroup @params
        }
    }

    # -------------------------------------------------------------------
    # Pass 1: groups.
    # -------------------------------------------------------------------
    Write-Host ""
    Write-Host "Pass 1: groups"
    $groupsCreated = 0
    $groupsSkipped = 0

    foreach ($g in $document.groups) {
        $existing = Find-ExistingByName -SamAccountName $g.samAccountName -Kind 'group'
        if ($existing) {
            Write-Verbose ("Group '{0}' already exists; skipping creation." -f $g.samAccountName)
            Add-ImportEvent -Action 'CreateGroup' -Subject $g.samAccountName -Outcome 'Skipped' -Detail 'Already exists'
            $groupsSkipped++
            continue
        }

        $ouPath = Initialize-OuPath -RelativeOuPath $g.relativeOuPath

        if ($PSCmdlet.ShouldProcess(("CN={0},{1}" -f $g.samAccountName, $ouPath), 'Create group')) {
            $createParams = @{
                Name           = $g.samAccountName
                SamAccountName = $g.samAccountName
                Path           = $ouPath
                GroupCategory  = $g.groupCategory
                GroupScope     = $g.groupScope
                Server         = $Server
            }
            if ($g.description)  { $createParams['Description'] = $g.description }
            if ($Credential)     { $createParams['Credential']  = $Credential }

            New-ADGroup @createParams
            Add-ImportEvent -Action 'CreateGroup' -Subject $g.samAccountName -Outcome 'Created' -Detail $ouPath
            $groupsCreated++
        }
    }
    Write-Host ("Groups: {0} created, {1} skipped" -f $groupsCreated, $groupsSkipped)

    # -------------------------------------------------------------------
    # Pass 2: users.
    # -------------------------------------------------------------------
    Write-Host ""
    Write-Host "Pass 2: users"
    $usersCreated = 0
    $usersSkipped = 0
    $generatedPasswords = New-Object System.Collections.Generic.List[object]
    $userSamToDn = @{}

    foreach ($u in $document.users) {
        $existing = Find-ExistingByName -SamAccountName $u.samAccountName -Kind 'user'
        if ($existing) {
            Write-Verbose ("User '{0}' already exists; skipping creation." -f $u.samAccountName)
            $userSamToDn[$u.samAccountName] = $existing.DistinguishedName
            Add-ImportEvent -Action 'CreateUser' -Subject $u.samAccountName -Outcome 'Skipped' -Detail 'Already exists'
            $usersSkipped++
            continue
        }

        $ouPath = Initialize-OuPath -RelativeOuPath $u.relativeOuPath

        # WHY: re-map the UPN to the target forest's DNS root. Keeping
        # the source-forest UPN in the dev forest would create UPN
        # suffixes that don't exist in the target, and would clash if
        # the prod forest is ever made reachable.
        $newUpn = $u.samAccountName + '@' + $targetDomain

        $attrs = $u.attributes
        $password = New-RandomPassword -Length $InitialPasswordLength
        $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force

        if ($PSCmdlet.ShouldProcess(("CN={0},{1}" -f $u.samAccountName, $ouPath), 'Create user')) {
            $createParams = @{
                Name                  = $u.samAccountName
                SamAccountName        = $u.samAccountName
                UserPrincipalName     = $newUpn
                Path                  = $ouPath
                AccountPassword       = $securePassword
                Enabled               = [bool]$attrs.accountEnabled
                ChangePasswordAtLogon = $true
                Server                = $Server
            }
            if ($Credential)           { $createParams['Credential']      = $Credential }

            # WHY: every optional attribute is written only when the
            # export captured a non-null value. AD-resident fields that
            # were null in the source forest must remain null in the
            # target - overwriting them with empty strings would corrupt
            # the dev-forest state.
            if ($attrs.givenName)              { $createParams['GivenName']            = $attrs.givenName }
            if ($attrs.sn)                     { $createParams['Surname']              = $attrs.sn }
            if ($attrs.initials)               { $createParams['Initials']             = $attrs.initials }
            if ($attrs.displayName)            { $createParams['DisplayName']          = $attrs.displayName }
            if ($attrs.mail)                   { $createParams['EmailAddress']         = $attrs.mail }
            if ($attrs.telephoneNumber)        { $createParams['OfficePhone']          = $attrs.telephoneNumber }
            if ($attrs.mobile)                 { $createParams['MobilePhone']          = $attrs.mobile }
            if ($attrs.office)                 { $createParams['Office']               = $attrs.office }
            if ($attrs.department)             { $createParams['Department']           = $attrs.department }
            if ($attrs.title)                  { $createParams['Title']                = $attrs.title }
            if ($attrs.company)                { $createParams['Company']              = $attrs.company }
            if ($attrs.description)            { $createParams['Description']          = $attrs.description }
            if ($attrs.employeeId)             { $createParams['EmployeeID']           = $attrs.employeeId }
            if ($attrs.employeeNumber)         { $createParams['EmployeeNumber']       = $attrs.employeeNumber }

            if ($attrs.accountExpirationDateUtc) {
                $createParams['AccountExpirationDate'] = [datetime]::Parse($attrs.accountExpirationDateUtc).ToUniversalTime()
            }

            # OtherAttributes for fields that don't have a friendly
            # New-ADUser parameter: personalTitle, info, employeeType,
            # physicalDeliveryOfficeName.
            $other = @{}
            if ($attrs.personalTitle)          { $other['personalTitle']            = $attrs.personalTitle }
            if ($attrs.info)                   { $other['info']                     = $attrs.info }
            if ($attrs.employeeType)           { $other['employeeType']             = $attrs.employeeType }
            if ($attrs.physicalDeliveryOffice) { $other['physicalDeliveryOfficeName'] = $attrs.physicalDeliveryOffice }
            if ($other.Count -gt 0)            { $createParams['OtherAttributes']   = $other }

            New-ADUser @createParams

            # Capture the new DN for later membership pass.
            $lookupParams = @{ Identity = $u.samAccountName; Server = $Server }
            if ($Credential) { $lookupParams['Credential'] = $Credential }
            $createdUser = Get-ADUser @lookupParams
            $userSamToDn[$u.samAccountName] = $createdUser.DistinguishedName

            [void]$generatedPasswords.Add([pscustomobject]@{
                SamAccountName  = $u.samAccountName
                InitialPassword = $password
                Upn             = $newUpn
            })
            Add-ImportEvent -Action 'CreateUser' -Subject $u.samAccountName -Outcome 'Created' -Detail $newUpn
            $usersCreated++
        }
    }

    # -------------------------------------------------------------------
    # Pass 2b: manager re-mapping.
    # WHY: we cannot set manager during creation because the manager may
    # be created later in the same pass (alphabetical iteration doesn't
    # guarantee order). Second sweep after every user is in place.
    # -------------------------------------------------------------------
    Write-Host ""
    Write-Host "Pass 2b: manager re-mapping"
    $managersSet = 0
    foreach ($u in $document.users) {
        $attrs = $u.attributes
        if (-not $attrs.managerSamAccountName) { continue }
        if (-not $userSamToDn.ContainsKey($u.samAccountName)) { continue }
        if (-not $userSamToDn.ContainsKey($attrs.managerSamAccountName)) {
            Write-Warning ("Manager '{0}' for user '{1}' was not in the export; manager attribute left unset." -f $attrs.managerSamAccountName, $u.samAccountName)
            Add-ImportEvent -Action 'SetManager' -Subject $u.samAccountName -Outcome 'Skipped' -Detail ("manager '{0}' missing" -f $attrs.managerSamAccountName)
            continue
        }
        $managerDn = $userSamToDn[$attrs.managerSamAccountName]

        if ($PSCmdlet.ShouldProcess($u.samAccountName, ("Set manager to {0}" -f $managerDn))) {
            $setParams = @{
                Identity = $u.samAccountName
                Manager  = $managerDn
                Server   = $Server
            }
            if ($Credential) { $setParams['Credential'] = $Credential }
            Set-ADUser @setParams
            Add-ImportEvent -Action 'SetManager' -Subject $u.samAccountName -Outcome 'Set' -Detail $managerDn
            $managersSet++
        }
    }
    Write-Host ("Managers: {0} re-mapped" -f $managersSet)

    Write-Host ("Users: {0} created, {1} skipped" -f $usersCreated, $usersSkipped)

    # -------------------------------------------------------------------
    # Pass 3: memberships.
    # -------------------------------------------------------------------
    Write-Host ""
    Write-Host "Pass 3: memberships"
    $membershipsAdded   = 0
    $membershipsSkipped = 0

    # Build a lookup of group SAM -> DN in the target.
    $groupSamToDn = @{}
    foreach ($g in $document.groups) {
        $existing = Find-ExistingByName -SamAccountName $g.samAccountName -Kind 'group'
        if ($existing) { $groupSamToDn[$g.samAccountName] = $existing.DistinguishedName }
    }

    foreach ($g in $document.groups) {
        if (-not $groupSamToDn.ContainsKey($g.samAccountName)) {
            Write-Warning ("Group '{0}' not found in target; skipping memberships." -f $g.samAccountName)
            continue
        }
        $groupDn = $groupSamToDn[$g.samAccountName]

        $resolvedMembers = New-Object System.Collections.Generic.List[string]
        foreach ($member in $g.members) {
            if ([string]::IsNullOrWhiteSpace($member)) { continue }

            if ($member.StartsWith('group:')) {
                $name = $member.Substring(6)
                if ($groupSamToDn.ContainsKey($name)) {
                    [void]$resolvedMembers.Add($groupSamToDn[$name])
                }
                else {
                    Write-Warning ("Group member '{0}' of '{1}' was not created; skipping." -f $name, $g.samAccountName)
                    $membershipsSkipped++
                }
            }
            else {
                if ($userSamToDn.ContainsKey($member)) {
                    [void]$resolvedMembers.Add($userSamToDn[$member])
                }
                else {
                    Write-Warning ("User member '{0}' of '{1}' was not created; skipping." -f $member, $g.samAccountName)
                    $membershipsSkipped++
                }
            }
        }

        if ($resolvedMembers.Count -eq 0) { continue }

        if ($PSCmdlet.ShouldProcess($groupDn, ("Add {0} members" -f $resolvedMembers.Count))) {
            try {
                $addParams = @{
                    Identity = $groupDn
                    Members  = $resolvedMembers
                    Server   = $Server
                }
                if ($Credential) { $addParams['Credential'] = $Credential }
                Add-ADGroupMember @addParams
                $membershipsAdded += $resolvedMembers.Count
                Add-ImportEvent -Action 'AddGroupMembers' -Subject $g.samAccountName -Outcome 'Added' -Detail ("count={0}" -f $resolvedMembers.Count)
            }
            catch {
                # WHY: Add-ADGroupMember is all-or-nothing; if some
                # members are already in the group it fails the whole
                # call. Fall back to one-by-one with `Write-Verbose`
                # on the already-member ones.
                Write-Verbose ("Bulk add failed for '{0}'; falling back to per-member add." -f $g.samAccountName)
                foreach ($m in $resolvedMembers) {
                    try {
                        $addOneParams = @{
                            Identity = $groupDn
                            Members  = $m
                            Server   = $Server
                            ErrorAction = 'Stop'
                        }
                        if ($Credential) { $addOneParams['Credential'] = $Credential }
                        Add-ADGroupMember @addOneParams
                        $membershipsAdded++
                    }
                    catch {
                        Write-Verbose ("  '{0}' already member of '{1}' or other error: {2}" -f $m, $g.samAccountName, $_.Exception.Message)
                        $membershipsSkipped++
                    }
                }
            }
        }
    }
    Write-Host ("Memberships: {0} added, {1} skipped" -f $membershipsAdded, $membershipsSkipped)

    # -------------------------------------------------------------------
    # Write the generated-password CSV with a restrictive ACL.
    # -------------------------------------------------------------------
    if ($generatedPasswords.Count -gt 0) {
        $passwordCsv = "$InputPath.passwords.csv"
        if ($PSCmdlet.ShouldProcess($passwordCsv, 'Write generated-password CSV')) {
            $generatedPasswords | Export-Csv -Path $passwordCsv -NoTypeInformation -Encoding UTF8 -Force

            # WHY: the CSV contains plaintext initial passwords. Strip
            # the inherited NTFS ACLs and grant read access only to the
            # current user (and SYSTEM, so backup / AV still works).
            try {
                $acl = Get-Acl -Path $passwordCsv
                $acl.SetAccessRuleProtection($true, $false)
                $acl.Access | ForEach-Object { [void]$acl.RemoveAccessRule($_) }

                $rules = @(
                    [System.Security.AccessControl.FileSystemAccessRule]::new(
                        [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
                        'FullControl', 'Allow'),
                    [System.Security.AccessControl.FileSystemAccessRule]::new(
                        'NT AUTHORITY\SYSTEM', 'FullControl', 'Allow')
                )
                foreach ($r in $rules) { $acl.AddAccessRule($r) }
                Set-Acl -Path $passwordCsv -AclObject $acl
                Write-Host ("Password CSV: {0} (restricted ACL)" -f $passwordCsv)
            }
            catch {
                Write-Warning ("Failed to apply restrictive ACL to '{0}': {1}" -f $passwordCsv, $_.Exception.Message)
            }
        }
    }

    # -------------------------------------------------------------------
    # Final summary.
    # -------------------------------------------------------------------
    $eventBuffer | ConvertTo-Json -Depth 5 | Set-Content -Path $jsonEventLog -Encoding UTF8 -Force

    Write-Host ""
    Write-Host "Import complete."
    Write-Host ("  Target forest:      {0}" -f $targetDomain)
    Write-Host ("  Target OU:          {0}" -f $TargetOu)
    Write-Host ("  Users created:      {0}" -f $usersCreated)
    Write-Host ("  Users skipped:      {0}" -f $usersSkipped)
    Write-Host ("  Managers set:       {0}" -f $managersSet)
    Write-Host ("  Groups created:     {0}" -f $groupsCreated)
    Write-Host ("  Groups skipped:     {0}" -f $groupsSkipped)
    Write-Host ("  Memberships added:  {0}" -f $membershipsAdded)
    Write-Host ("  Memberships skipped:{0}" -f $membershipsSkipped)
    Write-Host ("  Transcript:         {0}" -f $transcriptLog)
    Write-Host ("  Event log (JSON):   {0}" -f $jsonEventLog)
}
finally {
    Stop-Transcript | Out-Null
}
