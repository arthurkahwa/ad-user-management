<#
.SYNOPSIS
    Exports users, groups, attributes, and group memberships from the
    `source-forest.local` production AD forest to a portable JSON file.

.DESCRIPTION
    Read-only against the source forest. Connects to the supplied DC
    over raw LDAP (port 389 by default, or 636 with -UseLdaps),
    enumerates user and group objects under the (optional) search base,
    skips system / built-in accounts and disabled accounts (unless
    asked to include them), and emits a JSON document conforming to
    schema version 1 (see deploy/ad/README.md).

    The JSON is the integration contract with Import-AdUsersAndGroups.ps1.
    Designed for the M7 precursor flow: prod -> dev forest copy with
    safety checks. Never exports password-related attributes.

    This script uses `System.DirectoryServices.DirectoryEntry` and
    `System.DirectoryServices.DirectorySearcher` exclusively and does
    NOT require the ActiveDirectory PowerShell module (no RSAT) or
    Active Directory Web Services (ADWS, port 9389) on the target DC.
    Only the standard LDAP port (389 cleartext, or 636 with TLS) must
    be reachable.

.PARAMETER Server
    Hostname of a domain controller in the source forest (e.g.
    `dc01`). The script asserts the DC belongs to
    `source-forest.local` before reading anything; it refuses to run
    against any other forest. Mandatory.

.PARAMETER SearchBase
    Distinguished name of the OU subtree to export. Omit (default
    $null) to enumerate the entire domain (rootDSE's
    defaultNamingContext). Lets the operator scope a single OU for
    testing.

.PARAMETER OutputPath
    Filesystem path the JSON document is written to. Existing files
    are overwritten. Mandatory.

.PARAMETER IncludeDisabled
    Switch. When set, disabled user accounts are included in the
    export. Default behaviour skips them - dev forests don't need
    ballast.

.PARAMETER Credential
    PSCredential bound when contacting the source DC. Omit to use the
    current Windows identity (typical when run from a domain-joined
    workstation).

.PARAMETER UseLdaps
    Switch. When set, the script binds to the DC on port 636 over TLS
    (LDAPS). Default behaviour binds on port 389 (cleartext LDAP).
    Use -UseLdaps in environments where cleartext LDAP is disallowed
    by policy.

.EXAMPLE
    .\Export-AdUsersAndGroups.ps1 -Server dc01 `
        -OutputPath C:\Temp\export.json

    Export every active user and non-built-in group from the entire
    `source-forest.local` domain over cleartext LDAP (port 389).

.EXAMPLE
    .\Export-AdUsersAndGroups.ps1 -Server dc01 `
        -SearchBase 'OU=Engineering,DC=source-forest,DC=local' `
        -OutputPath C:\Temp\engineering.json -IncludeDisabled `
        -UseLdaps

    Export a single OU including disabled accounts over LDAPS (port
    636), for round-trip testing of the import script in an
    environment where cleartext LDAP is blocked.

.NOTES
    The ActiveDirectory PowerShell module is NOT required. This
    script works on any Windows machine without RSAT installed and
    does NOT require ADWS (Active Directory Web Services, port 9389)
    on the target DC. Only the LDAP port (389, or 636 with -UseLdaps)
    must be reachable.

    PowerShell 5.1+ compatible (some customer DCs still run Windows
    Server 2019). Read-only against the source domain.

    A transcript log is written to
    `$env:TEMP\ad-export-<timestamp>.log` for the audit trail.

    Password-related attributes are NEVER captured. The curated
    property list is the primary control; a forbidden-property
    assertion at JSON-serialise time provides defence-in-depth.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $Server = $env:COMPUTERNAME,

    [Parameter(Mandatory = $false)]
    [string] $SearchBase = $null,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [Parameter(Mandatory = $false)]
    [switch] $IncludeDisabled,

    [Parameter(Mandatory = $false)]
    [System.Management.Automation.PSCredential] $Credential,

    [Parameter(Mandatory = $false)]
    [switch] $UseLdaps
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

# WHY: System.DirectoryServices ships with .NET Framework on every
# Windows machine; the explicit Add-Type ensures the assembly is loaded
# even in stripped-down runspaces.
Add-Type -AssemblyName System.DirectoryServices

# -----------------------------------------------------------------------
# Helper functions.
# -----------------------------------------------------------------------

function New-LdapDirectoryEntry {
    <#
    .SYNOPSIS
    Build a DirectoryEntry against the given LDAP path using either
    the current Windows identity or the supplied PSCredential.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $false)]
        [System.Management.Automation.PSCredential] $Credential
    )

    if ($Credential) {
        $netCred = $Credential.GetNetworkCredential()
        # WHY: include the domain portion when the caller supplied
        # `DOMAIN\user`; PSCredential splits it out into .Domain
        # automatically.
        $userName = if ($netCred.Domain) {
            ('{0}\{1}' -f $netCred.Domain, $netCred.UserName)
        }
        else {
            $netCred.UserName
        }
        return New-Object System.DirectoryServices.DirectoryEntry(
            $Path,
            $userName,
            $netCred.Password
        )
    }

    return New-Object System.DirectoryServices.DirectoryEntry($Path)
}

function Get-StringProperty {
    <#
    .SYNOPSIS
    Read a single-valued string property from a DirectorySearcher
    result. Returns $null when absent or empty.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [System.DirectoryServices.SearchResult] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not $Result.Properties.Contains($Name)) { return $null }
    if ($Result.Properties[$Name].Count -eq 0)   { return $null }
    $value = $Result.Properties[$Name][0]
    if ($null -eq $value) { return $null }
    $str = [string]$value
    if ([string]::IsNullOrEmpty($str)) { return $null }
    return $str
}

function Get-IntProperty {
    <#
    .SYNOPSIS
    Read an integer property from a DirectorySearcher result. Returns
    $null when absent.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [System.DirectoryServices.SearchResult] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not $Result.Properties.Contains($Name)) { return $null }
    if ($Result.Properties[$Name].Count -eq 0)   { return $null }
    return [int]$Result.Properties[$Name][0]
}

function Get-LongProperty {
    <#
    .SYNOPSIS
    Read a 64-bit integer property (e.g. accountExpires,
    lastLogonTimestamp) from a DirectorySearcher result. Returns $null
    when absent.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [System.DirectoryServices.SearchResult] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not $Result.Properties.Contains($Name)) { return $null }
    if ($Result.Properties[$Name].Count -eq 0)   { return $null }
    return [int64]$Result.Properties[$Name][0]
}

function Convert-FileTimeToUtc {
    <#
    .SYNOPSIS
    Convert an AD int64 file-time (100ns ticks since 1601-01-01 UTC)
    to a [datetime] in UTC. Treats 0 and Int64.MaxValue as "never" and
    returns $null.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [Nullable[int64]] $FileTime
    )

    if ($null -eq $FileTime)                       { return $null }
    if ($FileTime -eq 0)                           { return $null }
    if ($FileTime -eq 9223372036854775807)         { return $null }
    try {
        return [datetime]::FromFileTimeUtc($FileTime)
    }
    catch {
        return $null
    }
}

function Get-DateTimeProperty {
    <#
    .SYNOPSIS
    Read an AD date attribute, transparently handling the three shapes
    AD exposes: native System.DateTime (e.g. whenCreated when
    DirectorySearcher unwraps the Generalized Time), Int64 file-time
    (accountExpires, lastLogonTimestamp), and Generalized Time strings
    ("yyyyMMddHHmmss.0Z"). Returns $null when absent or sentinel.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [System.DirectoryServices.SearchResult] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if (-not $Result.Properties.Contains($Name)) { return $null }
    if ($Result.Properties[$Name].Count -eq 0)   { return $null }
    $raw = $Result.Properties[$Name][0]
    if ($null -eq $raw) { return $null }

    if ($raw -is [datetime]) {
        return $raw.ToUniversalTime()
    }
    if ($raw -is [int64] -or $raw -is [long]) {
        return (Convert-FileTimeToUtc -FileTime ([int64]$raw))
    }
    if ($raw -is [string]) {
        $s = [string]$raw
        if ([string]::IsNullOrEmpty($s)) { return $null }
        try {
            $parsed = [datetime]::ParseExact(
                $s, 'yyyyMMddHHmmss.0Z', $null,
                [System.Globalization.DateTimeStyles]::AssumeUniversal -bor `
                    [System.Globalization.DateTimeStyles]::AdjustToUniversal
            )
            return $parsed.ToUniversalTime()
        }
        catch {
            return $null
        }
    }
    return $null
}

function ConvertTo-IsoDate {
    <#
    .SYNOPSIS
    Format a UTC [datetime] in ISO-8601 round-trip form, or return
    $null if the input is $null.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Value
    )

    if ($null -eq $Value)         { return $null }
    if (-not ($Value -is [datetime])) { return $null }
    return $Value.ToUniversalTime().ToString('o')
}

function Get-RelativeOuPath {
    <#
    .SYNOPSIS
    Strip the leaf CN and the domain DN suffix from a distinguished
    name, returning just the OU portion (e.g.
    "OU=Engineering,OU=Users"). Returns $null when the DN is empty.

    LIMITATION: this is a string-level strip and does not honour DN
    escaping for commas inside CN values (e.g. CN="Doe\, John"). The
    import script uses the same string-level approach, so the two
    scripts agree by construction. If a future export ever encounters
    escaped commas in CN/OU components, this helper should be replaced
    by a proper DN parser on both sides.
    #>
    param(
        [Parameter(Mandatory = $false)]
        [AllowNull()]
        [AllowEmptyString()]
        [string] $DistinguishedName,

        [Parameter(Mandatory = $true)]
        [string] $DomainDn
    )

    if ([string]::IsNullOrEmpty($DistinguishedName)) { return $null }

    # WHY: drop the leading CN=<leaf>, leaving the parent path.
    $withoutLeaf = $DistinguishedName -replace '^CN=[^,]+,', ''
    $suffix = ',' + $DomainDn
    if ($withoutLeaf.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $withoutLeaf.Substring(0, $withoutLeaf.Length - $suffix.Length)
    }
    return $withoutLeaf
}

function Get-EscapedLdapFilterValue {
    <#
    .SYNOPSIS
    Escape an LDAP filter value per RFC 4515 so a distinguished name
    can be embedded verbatim in a `(distinguishedName=...)` filter.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $Value.ToCharArray()) {
        switch ($ch) {
            '\' { [void]$sb.Append('\5c') }
            '*' { [void]$sb.Append('\2a') }
            '(' { [void]$sb.Append('\28') }
            ')' { [void]$sb.Append('\29') }
            "`0" { [void]$sb.Append('\00') }
            default { [void]$sb.Append($ch) }
        }
    }
    return $sb.ToString()
}

function Resolve-DnToSamAccountName {
    <#
    .SYNOPSIS
    Resolve a single DN to a hashtable describing the object's
    sAMAccountName and whether it is a group. Returns $null on
    failure.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string] $DistinguishedName,

        [Parameter(Mandatory = $true)]
        [string] $PathPrefix,

        [Parameter(Mandatory = $false)]
        [System.Management.Automation.PSCredential] $Credential
    )

    $path  = $PathPrefix + $DistinguishedName
    $entry = $null
    try {
        $entry = New-LdapDirectoryEntry -Path $path -Credential $Credential
        # WHY: a base-scope searcher on the object itself reads only
        # the two properties we care about and avoids the overhead of
        # filtering across the whole subtree.
        $searcher = New-Object System.DirectoryServices.DirectorySearcher($entry)
        try {
            $searcher.Filter      = '(objectClass=*)'
            $searcher.SearchScope = [System.DirectoryServices.SearchScope]::Base
            $searcher.PageSize    = 0
            $searcher.SizeLimit   = 1
            [void]$searcher.PropertiesToLoad.Add('sAMAccountName')
            [void]$searcher.PropertiesToLoad.Add('objectClass')
            $found = $searcher.FindOne()
            if ($null -eq $found) { return $null }

            $sam   = Get-StringProperty -Result $found -Name 'sAMAccountName'
            $isGrp = $false
            if ($found.Properties.Contains('objectClass')) {
                foreach ($c in $found.Properties['objectClass']) {
                    if ([string]$c -eq 'group') { $isGrp = $true; break }
                }
            }
            return @{ SamAccountName = $sam; IsGroup = $isGrp }
        }
        finally {
            $searcher.Dispose()
        }
    }
    catch {
        return $null
    }
    finally {
        if ($entry) { $entry.Dispose() }
    }
}

# -----------------------------------------------------------------------
# Main body.
# -----------------------------------------------------------------------

try {
    # -------------------------------------------------------------------
    # Filtering tables - easy to amend without touching the body.
    # -------------------------------------------------------------------

    # WHY: pattern-based skip list for system / service accounts. The
    # default RID-500 (`Administrator`), `krbtgt`, `Guest`,
    # `DefaultAccount`, and the IIS / SQL service-account legacy
    # prefixes are excluded - they belong to the directory itself, not
    # to the human staff the dev forest needs.
    $systemAccountPattern = '^(krbtgt|Guest|Administrator|DefaultAccount)$|^IUSR_|^IWAM_|^SQLServer|^MSOL_'

    # WHY: built-in groups are forest-managed and cannot be safely
    # recreated in another forest by name. Skip anything that lives
    # under `CN=Builtin,...` plus the well-known privileged groups by
    # SamAccountName.
    $builtInGroupPattern = '^(Domain Admins|Enterprise Admins|Schema Admins|Administrators|Users|Guests|Replicator)$'

    # WHY: belt-and-braces against the curated property list ever
    # growing to include credential material. We never want password
    # hashes leaving the source forest, not even in memory after the
    # export. This list is asserted against the emitted attributes
    # hashtable just before JSON serialisation.
    $forbiddenProperties = @(
        'unicodepwd', 'lmpwdhistory', 'ntpwdhistory', 'dbcspwd',
        'supplementalcredentials', 'msds-keycredentiallink'
    )

    # -------------------------------------------------------------------
    # Build the LDAP path prefix and connect to rootDSE.
    # -------------------------------------------------------------------
    $pathPrefix = if ($UseLdaps.IsPresent) {
        'LDAP://' + $Server + ':636/'
    }
    else {
        'LDAP://' + $Server + '/'
    }
    $rootPath = $pathPrefix + 'RootDSE'

    Write-Host ("Connecting to rootDSE via {0}..." -f $rootPath)
    $rootDse = $null
    try {
        $rootDse = New-LdapDirectoryEntry -Path $rootPath -Credential $Credential
        # WHY: force the bind by touching a property; constructing a
        # DirectoryEntry by itself is lazy and won't raise auth or
        # network errors.
        $defaultNc = $null
        if ($rootDse.Properties['defaultNamingContext'].Count -gt 0) {
            $defaultNc = [string]$rootDse.Properties['defaultNamingContext'][0]
        }
        $dnsHost = $null
        if ($rootDse.Properties['dnsHostName'].Count -gt 0) {
            $dnsHost = [string]$rootDse.Properties['dnsHostName'][0]
        }
        $ldapSvc = $null
        if ($rootDse.Properties['ldapServiceName'].Count -gt 0) {
            $ldapSvc = [string]$rootDse.Properties['ldapServiceName'][0]
        }
    }
    catch {
        $port = if ($UseLdaps.IsPresent) { 636 } else { 389 }
        throw ("Could not bind to '{0}' on port {1}: {2}" -f $Server, $port, $_.Exception.Message)
    }

    if ([string]::IsNullOrEmpty($defaultNc)) {
        throw ("rootDSE on '{0}' did not return a defaultNamingContext attribute." -f $Server)
    }

    # -------------------------------------------------------------------
    # Forest safety check - refuse to read anything until we have proved
    # the DC belongs to the expected source forest.
    # -------------------------------------------------------------------
    Write-Host "Verifying source forest..."
    if (-not ($defaultNc.ToLowerInvariant().EndsWith('dc=source-forest,dc=local'))) {
        throw ("Refusing to export from '{0}' - this script only reads from source-forest.local" -f $defaultNc)
    }

    # WHY: dnsHostName and ldapServiceName are advisory belt-and-braces
    # logging. The hard gate is on defaultNamingContext above.
    Write-Host ("Connected to DC '{0}' (defaultNamingContext = {1})" -f $Server, $defaultNc)
    if ($dnsHost)  { Write-Host ("  dnsHostName:     {0}" -f $dnsHost) }
    if ($ldapSvc)  { Write-Host ("  ldapServiceName: {0}" -f $ldapSvc) }

    # WHY: derive the source forest DNS name from defaultNamingContext
    # so the emitted JSON matches what the previous AD-module version
    # produced ($sourceDomain.DNSRoot). Convert "DC=source-forest,
    # DC=local" -> "source-forest.local".
    $sourceForestDns = ($defaultNc -split ',' |
        Where-Object { $_ -match '^DC=' } |
        ForEach-Object { $_.Substring(3) }) -join '.'

    $domainDn = $defaultNc

    # Default -SearchBase to the whole domain when the operator did not
    # supply one.
    if ([string]::IsNullOrEmpty($SearchBase)) {
        $SearchBase = $domainDn
    }

    # -------------------------------------------------------------------
    # Curated user property list - never request all attributes.
    # These are the LDAP attribute names, matching the original
    # AD-module property names where they differ:
    #   sn                          <- Surname
    #   mail                        <- Mail
    #   physicalDeliveryOfficeName  <- Office / physicalDeliveryOfficeName
    #   userAccountControl          <- Enabled (computed from bit 0x2)
    #   accountExpires              <- AccountExpirationDate
    #   whenCreated                 <- WhenCreated
    #   lastLogonTimestamp          <- LastLogonDate
    #   info                        <- Info ("notes" in ADUC)
    # -------------------------------------------------------------------
    $userLdapProperties = @(
        'sAMAccountName',
        'userPrincipalName',
        'distinguishedName',
        'objectGUID',
        'objectSid',
        'givenName',
        'sn',
        'initials',
        'displayName',
        'personalTitle',
        'mail',
        'telephoneNumber',
        'mobile',
        'physicalDeliveryOfficeName',
        'department',
        'company',
        'title',
        'manager',
        'userAccountControl',
        'accountExpires',
        'whenCreated',
        'lastLogonTimestamp',
        'description',
        'info',
        'employeeID',
        'employeeNumber',
        'employeeType'
    )

    # -------------------------------------------------------------------
    # Enumerate users.
    # -------------------------------------------------------------------
    Write-Host ("Enumerating users under '{0}'..." -f $SearchBase)
    $userSearchRoot = $null
    $userSearcher   = $null
    $userResults    = $null
    $rawUsers       = New-Object System.Collections.Generic.List[object]
    try {
        $userSearchRoot = New-LdapDirectoryEntry -Path ($pathPrefix + $SearchBase) -Credential $Credential
        $userSearcher   = New-Object System.DirectoryServices.DirectorySearcher($userSearchRoot)
        $userSearcher.Filter          = '(&(objectCategory=person)(objectClass=user))'
        $userSearcher.PageSize        = 1000
        $userSearcher.SizeLimit       = 0
        $userSearcher.SearchScope     = [System.DirectoryServices.SearchScope]::Subtree
        $userSearcher.ReferralChasing = [System.DirectoryServices.ReferralChasingOption]::None
        foreach ($p in $userLdapProperties) {
            [void]$userSearcher.PropertiesToLoad.Add($p)
        }

        $userResults = $userSearcher.FindAll()
        foreach ($r in $userResults) {
            [void]$rawUsers.Add($r)
        }
    }
    finally {
        if ($userResults)    { $userResults.Dispose() }
        if ($userSearcher)   { $userSearcher.Dispose() }
        if ($userSearchRoot) { $userSearchRoot.Dispose() }
    }

    Write-Host ("LDAP returned {0} user object(s)." -f $rawUsers.Count)

    # -------------------------------------------------------------------
    # First pass: filter, decode attributes, collect manager DNs.
    # -------------------------------------------------------------------
    $exportedUsers     = New-Object System.Collections.Generic.List[object]
    $skippedSystem     = 0
    $skippedDisabled   = 0
    $managerDnsToResolve = New-Object System.Collections.Generic.HashSet[string]

    foreach ($r in $rawUsers) {
        $sam = Get-StringProperty -Result $r -Name 'sAMAccountName'
        if ([string]::IsNullOrEmpty($sam)) {
            # WHY: an object without a sAMAccountName cannot survive
            # round-tripping into the dev forest; drop it.
            continue
        }
        if ($sam -match $systemAccountPattern) {
            $skippedSystem++
            continue
        }

        $uac     = Get-IntProperty -Result $r -Name 'userAccountControl'
        $enabled = $true
        if ($null -ne $uac) {
            # ADS_UF_ACCOUNTDISABLE = 0x2; bit clear = enabled.
            $enabled = (($uac -band 0x2) -eq 0)
        }
        if (-not $IncludeDisabled.IsPresent -and -not $enabled) {
            $skippedDisabled++
            continue
        }

        $dn = Get-StringProperty -Result $r -Name 'distinguishedName'
        $relativeOuPath = Get-RelativeOuPath -DistinguishedName $dn -DomainDn $domainDn

        $objectGuid = $null
        if ($r.Properties.Contains('objectGUID') -and $r.Properties['objectGUID'].Count -gt 0) {
            $bytes = [byte[]]$r.Properties['objectGUID'][0]
            $objectGuid = (New-Object System.Guid(,$bytes)).ToString()
        }

        $accountExpiresUtc = Convert-FileTimeToUtc -FileTime (Get-LongProperty -Result $r -Name 'accountExpires')
        $whenCreatedUtc    = Get-DateTimeProperty -Result $r -Name 'whenCreated'

        $managerDn = Get-StringProperty -Result $r -Name 'manager'
        if (-not [string]::IsNullOrEmpty($managerDn)) {
            [void]$managerDnsToResolve.Add($managerDn)
        }

        $entry = [ordered]@{
            samAccountName    = $sam
            userPrincipalName = Get-StringProperty -Result $r -Name 'userPrincipalName'
            objectGuid        = $objectGuid
            relativeOuPath    = $relativeOuPath
            attributes        = [ordered]@{
                givenName                = Get-StringProperty -Result $r -Name 'givenName'
                sn                       = Get-StringProperty -Result $r -Name 'sn'
                initials                 = Get-StringProperty -Result $r -Name 'initials'
                displayName              = Get-StringProperty -Result $r -Name 'displayName'
                personalTitle            = Get-StringProperty -Result $r -Name 'personalTitle'
                mail                     = Get-StringProperty -Result $r -Name 'mail'
                telephoneNumber          = Get-StringProperty -Result $r -Name 'telephoneNumber'
                mobile                   = Get-StringProperty -Result $r -Name 'mobile'
                # WHY: the original AD-module export emitted both
                # `office` and `physicalDeliveryOffice` keys from the
                # same source attribute (the AD module surfaced them
                # as synonyms). Preserve that twin shape verbatim so
                # the JSON schema is byte-for-byte unchanged.
                office                   = Get-StringProperty -Result $r -Name 'physicalDeliveryOfficeName'
                physicalDeliveryOffice   = Get-StringProperty -Result $r -Name 'physicalDeliveryOfficeName'
                department               = Get-StringProperty -Result $r -Name 'department'
                title                    = Get-StringProperty -Result $r -Name 'title'
                company                  = Get-StringProperty -Result $r -Name 'company'
                managerSamAccountName    = $null  # resolved in pass 2 below
                accountEnabled           = [bool]$enabled
                accountExpirationDateUtc = ConvertTo-IsoDate -Value $accountExpiresUtc
                whenCreatedUtc           = ConvertTo-IsoDate -Value $whenCreatedUtc
                description              = Get-StringProperty -Result $r -Name 'description'
                info                     = Get-StringProperty -Result $r -Name 'info'
                employeeId               = Get-StringProperty -Result $r -Name 'employeeID'
                employeeNumber           = Get-StringProperty -Result $r -Name 'employeeNumber'
                employeeType             = Get-StringProperty -Result $r -Name 'employeeType'
            }
            # WHY: stash the source manager DN on a non-emitted field
            # so the second pass can fill in managerSamAccountName
            # without re-reading the LDAP result.
            _sourceManagerDn  = $managerDn
        }

        [void]$exportedUsers.Add([pscustomobject]$entry)
    }

    # -------------------------------------------------------------------
    # Second pass: resolve manager DNs to sAMAccountNames (batched,
    # deduped).
    # -------------------------------------------------------------------
    $managerDnToSam = @{}
    $unresolvedManagerDns = New-Object System.Collections.Generic.HashSet[string]
    if ($managerDnsToResolve.Count -gt 0) {
        Write-Host ("Resolving {0} unique manager DN(s)..." -f $managerDnsToResolve.Count)
        foreach ($mgrDn in $managerDnsToResolve) {
            $resolved = Resolve-DnToSamAccountName -DistinguishedName $mgrDn -PathPrefix $pathPrefix -Credential $Credential
            if ($null -ne $resolved -and -not [string]::IsNullOrEmpty($resolved.SamAccountName)) {
                $managerDnToSam[$mgrDn] = $resolved.SamAccountName
            }
            else {
                [void]$unresolvedManagerDns.Add($mgrDn)
            }
        }
    }

    foreach ($unresolved in $unresolvedManagerDns) {
        Write-Warning ("Manager DN '{0}' could not be resolved; managerSamAccountName will be null." -f $unresolved)
    }

    foreach ($u in $exportedUsers) {
        $srcMgr = $u._sourceManagerDn
        if (-not [string]::IsNullOrEmpty($srcMgr) -and $managerDnToSam.ContainsKey($srcMgr)) {
            $u.attributes.managerSamAccountName = $managerDnToSam[$srcMgr]
        }
    }

    Write-Host ("Users: {0} retained, {1} system, {2} disabled" -f $exportedUsers.Count, $skippedSystem, $skippedDisabled)

    # -------------------------------------------------------------------
    # Enumerate groups.
    # -------------------------------------------------------------------
    Write-Host ("Enumerating groups under '{0}'..." -f $SearchBase)
    $groupSearchRoot = $null
    $groupSearcher   = $null
    $groupResults    = $null
    $rawGroups       = New-Object System.Collections.Generic.List[object]
    $groupLdapProperties = @(
        'sAMAccountName',
        'distinguishedName',
        'description',
        'groupType',
        'member'
    )

    try {
        $groupSearchRoot = New-LdapDirectoryEntry -Path ($pathPrefix + $SearchBase) -Credential $Credential
        $groupSearcher   = New-Object System.DirectoryServices.DirectorySearcher($groupSearchRoot)
        $groupSearcher.Filter          = '(objectCategory=group)'
        $groupSearcher.PageSize        = 1000
        $groupSearcher.SizeLimit       = 0
        $groupSearcher.SearchScope     = [System.DirectoryServices.SearchScope]::Subtree
        $groupSearcher.ReferralChasing = [System.DirectoryServices.ReferralChasingOption]::None
        foreach ($p in $groupLdapProperties) {
            [void]$groupSearcher.PropertiesToLoad.Add($p)
        }

        $groupResults = $groupSearcher.FindAll()
        foreach ($r in $groupResults) {
            [void]$rawGroups.Add($r)
        }
    }
    finally {
        if ($groupResults)    { $groupResults.Dispose() }
        if ($groupSearcher)   { $groupSearcher.Dispose() }
        if ($groupSearchRoot) { $groupSearchRoot.Dispose() }
    }

    Write-Host ("LDAP returned {0} group object(s)." -f $rawGroups.Count)

    # WHY: collect every member DN across every retained group so we
    # can batch-resolve them in a single pass after filtering.
    $exportedGroups   = New-Object System.Collections.Generic.List[object]
    $skippedBuiltIn   = 0
    $allMemberDns     = New-Object System.Collections.Generic.HashSet[string]
    $groupStagings    = New-Object System.Collections.Generic.List[object]

    foreach ($r in $rawGroups) {
        $sam = Get-StringProperty -Result $r -Name 'sAMAccountName'
        if ([string]::IsNullOrEmpty($sam)) { continue }

        if ($sam -match $builtInGroupPattern) {
            $skippedBuiltIn++
            continue
        }

        $dn = Get-StringProperty -Result $r -Name 'distinguishedName'
        if (-not [string]::IsNullOrEmpty($dn) -and $dn -match ',CN=Builtin,') {
            $skippedBuiltIn++
            continue
        }

        $relativeOuPath = Get-RelativeOuPath -DistinguishedName $dn -DomainDn $domainDn

        $groupTypeInt = Get-IntProperty -Result $r -Name 'groupType'
        $groupCategory = 'Distribution'
        $groupScope    = 'Global'
        if ($null -ne $groupTypeInt) {
            # WHY: groupType is a bit-mask. The high bit (0x80000000,
            # which surfaces as a negative Int32) signals a security
            # group; clear means distribution. The low bits 0x2 / 0x4
            # / 0x8 select Global / DomainLocal / Universal.
            if (($groupTypeInt -band [int]0x80000000) -ne 0) {
                $groupCategory = 'Security'
            }
            if (($groupTypeInt -band 0x8) -ne 0) {
                $groupScope = 'Universal'
            }
            elseif (($groupTypeInt -band 0x4) -ne 0) {
                $groupScope = 'DomainLocal'
            }
            elseif (($groupTypeInt -band 0x2) -ne 0) {
                $groupScope = 'Global'
            }
        }

        $memberDns = New-Object System.Collections.Generic.List[string]
        if ($r.Properties.Contains('member')) {
            foreach ($m in $r.Properties['member']) {
                $memberDn = [string]$m
                if (-not [string]::IsNullOrEmpty($memberDn)) {
                    [void]$memberDns.Add($memberDn)
                    [void]$allMemberDns.Add($memberDn)
                }
            }
        }

        $staging = [pscustomobject]@{
            SamAccountName = $sam
            RelativeOuPath = $relativeOuPath
            Description    = Get-StringProperty -Result $r -Name 'description'
            GroupCategory  = $groupCategory
            GroupScope     = $groupScope
            MemberDns      = $memberDns
        }
        [void]$groupStagings.Add($staging)
    }

    # -------------------------------------------------------------------
    # Resolve all member DNs in a single deduped pass.
    # -------------------------------------------------------------------
    $memberDnToResolved = @{}  # DN -> @{ SamAccountName = '...'; IsGroup = $bool }
    $unresolvedMemberDns = New-Object System.Collections.Generic.HashSet[string]
    if ($allMemberDns.Count -gt 0) {
        Write-Host ("Resolving {0} unique group-member DN(s)..." -f $allMemberDns.Count)
        foreach ($memberDn in $allMemberDns) {
            $resolved = Resolve-DnToSamAccountName -DistinguishedName $memberDn -PathPrefix $pathPrefix -Credential $Credential
            if ($null -ne $resolved -and -not [string]::IsNullOrEmpty($resolved.SamAccountName)) {
                $memberDnToResolved[$memberDn] = $resolved
            }
            else {
                [void]$unresolvedMemberDns.Add($memberDn)
            }
        }
    }

    foreach ($unresolved in $unresolvedMemberDns) {
        Write-Warning ("Group member DN '{0}' could not be resolved; will be omitted from the export." -f $unresolved)
    }

    $totalMemberships = 0
    foreach ($s in $groupStagings) {
        $memberNames = New-Object System.Collections.Generic.List[string]
        foreach ($memberDn in $s.MemberDns) {
            if (-not $memberDnToResolved.ContainsKey($memberDn)) { continue }
            $r = $memberDnToResolved[$memberDn]
            if ($r.IsGroup) {
                [void]$memberNames.Add('group:' + $r.SamAccountName)
            }
            else {
                [void]$memberNames.Add($r.SamAccountName)
            }
        }
        $totalMemberships += $memberNames.Count

        $entry = [ordered]@{
            samAccountName = $s.SamAccountName
            relativeOuPath = $s.RelativeOuPath
            description    = $s.Description
            groupCategory  = $s.GroupCategory
            groupScope     = $s.GroupScope
            members        = @($memberNames)
        }
        [void]$exportedGroups.Add([pscustomobject]$entry)
    }

    Write-Host ("Groups: {0} retained, {1} built-in" -f $exportedGroups.Count, $skippedBuiltIn)
    Write-Host ("Memberships: {0}" -f $totalMemberships)

    # -------------------------------------------------------------------
    # Defence-in-depth: assert no forbidden property names leaked into
    # any user's attributes hashtable. The curated property list above
    # is the primary control; this catches future edits that might add
    # `-Properties *` semantics or similar.
    # -------------------------------------------------------------------
    foreach ($u in $exportedUsers) {
        foreach ($k in @($u.attributes.Keys)) {
            if ($forbiddenProperties -contains $k.ToLowerInvariant()) {
                throw ("Forbidden property '{0}' appeared in the export for user '{1}'. Aborting." -f $k, $u.samAccountName)
            }
        }
    }

    # -------------------------------------------------------------------
    # Drop the internal _sourceManagerDn helper field before serialising
    # so it doesn't surface in the JSON.
    # -------------------------------------------------------------------
    $finalUsers = New-Object System.Collections.Generic.List[object]
    foreach ($u in $exportedUsers) {
        $entry = [ordered]@{
            samAccountName    = $u.samAccountName
            userPrincipalName = $u.userPrincipalName
            objectGuid        = $u.objectGuid
            relativeOuPath    = $u.relativeOuPath
            attributes        = $u.attributes
        }
        [void]$finalUsers.Add([pscustomobject]$entry)
    }

    # -------------------------------------------------------------------
    # Compose the JSON document.
    # -------------------------------------------------------------------
    # Build the JSON document incrementally so any value-expression failure
    # pinpoints the exact key + line, not just the opening of the literal.
    $document = [ordered]@{}

    $documentBuildSteps = @(
        @{ Key = 'schemaVersion';  Value = { 1 } }
        @{ Key = 'exportedAtUtc';  Value = { (Get-Date).ToUniversalTime().ToString('o') } }
        @{ Key = 'sourceForest';   Value = { $sourceForestDns } }
        @{ Key = 'sourceDc';       Value = { $Server } }
        @{ Key = 'searchBase';     Value = { $SearchBase } }
        @{ Key = 'userCount';      Value = { $finalUsers.Count } }
        @{ Key = 'groupCount';     Value = { $exportedGroups.Count } }
        @{ Key = 'users';          Value = { ,@($finalUsers.ToArray()) } }
        @{ Key = 'groups';         Value = { ,@($exportedGroups.ToArray()) } }
    )

    foreach ($step in $documentBuildSteps) {
        try {
            $document[$step.Key] = & $step.Value
        }
        catch {
            Write-Host ("[document build] Failed at key '{0}'" -f $step.Key) -ForegroundColor Red
            Write-Host ("[document build] Exception: {0}" -f $_.Exception.GetType().FullName) -ForegroundColor Red
            Write-Host ("[document build] Message:   {0}" -f $_.Exception.Message) -ForegroundColor Red
            if ($_.Exception.InnerException) {
                Write-Host ("[document build] Inner:     {0}: {1}" -f $_.Exception.InnerException.GetType().FullName, $_.Exception.InnerException.Message) -ForegroundColor Red
            }
            throw
        }
    }

    # WHY: -Depth 8 covers users/attributes (2 levels) and groups/members
    # (1 level) with margin. Pretty-printed for easy human review of the
    # contract on disk before running the import.
    try {
        $json = $document | ConvertTo-Json -Depth 8
    }
    catch {
        Write-Host "[ConvertTo-Json] Failed" -ForegroundColor Red
        Write-Host ("[ConvertTo-Json] Exception: {0}" -f $_.Exception.GetType().FullName) -ForegroundColor Red
        Write-Host ("[ConvertTo-Json] Message:   {0}" -f $_.Exception.Message) -ForegroundColor Red
        if ($_.Exception.InnerException) {
            Write-Host ("[ConvertTo-Json] Inner:     {0}: {1}" -f $_.Exception.InnerException.GetType().FullName, $_.Exception.InnerException.Message) -ForegroundColor Red
        }
        throw
    }

    # WHY: Windows PowerShell 5.1's -Encoding utf8 writes UTF-8 *with*
    # BOM, which is what the import script (running on the same
    # platform) expects. PowerShell 7+ changed -Encoding utf8 to mean
    # *without* BOM, so on PS 7 we explicitly select utf8BOM to keep
    # the file format identical across versions.
    try {
        if ($PSVersionTable.PSVersion.Major -ge 6) {
            Set-Content -Path $OutputPath -Value $json -Encoding utf8BOM -Force
        }
        else {
            Set-Content -Path $OutputPath -Value $json -Encoding utf8 -Force
        }
    }
    catch {
        Write-Host ("[Set-Content] Failed writing to '{0}'" -f $OutputPath) -ForegroundColor Red
        Write-Host ("[Set-Content] Exception: {0}" -f $_.Exception.GetType().FullName) -ForegroundColor Red
        Write-Host ("[Set-Content] Message:   {0}" -f $_.Exception.Message) -ForegroundColor Red
        throw
    }

    $fileInfo = Get-Item -Path $OutputPath
    Write-Host ""
    Write-Host "Export complete."
    Write-Host ("  Output:        {0}" -f $fileInfo.FullName)
    Write-Host ("  Size:          {0:N0} bytes" -f $fileInfo.Length)
    Write-Host ("  Users:         {0}" -f $finalUsers.Count)
    Write-Host ("  Groups:        {0}" -f $exportedGroups.Count)
    Write-Host ("  Memberships:   {0}" -f $totalMemberships)
    Write-Host ("  Transcript:    {0}" -f $transcriptLog)
}
finally {
    if ($rootDse) { $rootDse.Dispose() }
    Stop-Transcript | Out-Null
}
