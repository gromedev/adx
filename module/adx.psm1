# ADx Module Loader
# Loads ADx.Cmdlets.dll into the Default ALC.
#
# Unlike the Graph module, there is no AlcInitializer and no Dependencies/ folder: ADx has no
# third-party runtime dependencies at all. System.DirectoryServices.Protocols is referenced with
# ExcludeAssets="runtime" and resolves from PowerShell's own app base, so there is nothing to
# broker and no teardown ordering to get wrong.

$ModuleRoot = $PSScriptRoot

# Load the main cmdlet assembly
$CmdletsDll = Join-Path $ModuleRoot 'ADx.Cmdlets.dll'
if (Test-Path $CmdletsDll) {
    Import-Module $CmdletsDll

    # Same-session upgrade guard. Assemblies never unload from the Default ALC, and
    # AssemblyVersion is deliberately pinned (see the csproj comment) so a re-import BINDS to
    # the already-loaded copy rather than failing - meaning an upgrade imported into a session
    # that already ran ADx silently executes the OLD code. The stamped InformationalVersion
    # (Directory.Build.props, from this manifest) is what makes the staleness detectable at all.
    $loadedAssembly = [System.AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { $_.GetName().Name -eq 'ADx.Cmdlets' } |
        Select-Object -First 1
    if ($loadedAssembly) {
        $informational = ($loadedAssembly.GetCustomAttributes(
                [System.Reflection.AssemblyInformationalVersionAttribute], $false) |
            Select-Object -First 1).InformationalVersion
        # The SDK appends "+<commit>" when a source revision is known; the release version is
        # the part before it.
        $loadedVersion = if ($informational) { ($informational -split '\+')[0] } else { $null }
        $manifestVersion = (Import-PowerShellDataFile (Join-Path $ModuleRoot 'adx.psd1')).ModuleVersion

        if ($loadedVersion -and $manifestVersion -and $loadedVersion -ne $manifestVersion) {
            Write-Warning ("ADx $loadedVersion is already loaded in this session, but this module is " +
                "$manifestVersion. PowerShell cannot unload assemblies; restart pwsh to run the new version.")
        }
    }
} else {
    Write-Error "ADx.Cmdlets.dll not found at $CmdletsDll. Did you run the build script?"
}

# Tab completion for common LDAP filters.
# objectCategory=user also matches computer accounts, since the computer class derives from
# user. The person/user pair is the one that means "human accounts".
$script:LdapFilterCompletions = @(
    @{ Text = '(&(objectCategory=person)(objectClass=user))';  Tip = 'User accounts (excludes computers)' }
    @{ Text = '(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))'
       Tip  = 'Enabled user accounts (server-side bitwise filter)' }
    @{ Text = '(objectCategory=group)';                        Tip = 'All groups' }
    @{ Text = '(objectCategory=computer)';                     Tip = 'Computer accounts' }
    @{ Text = '(objectClass=organizationalUnit)';              Tip = 'Organizational units' }
    @{ Text = '(objectClass=contact)';                         Tip = 'Contacts' }
    @{ Text = '(&(objectCategory=group)(groupType:1.2.840.113556.1.4.803:=2147483648))'
       Tip  = 'Security groups only' }
    @{ Text = '(adminCount=1)';                                Tip = 'Protected (AdminSDHolder) objects' }
    @{ Text = '(servicePrincipalName=*)';                      Tip = 'Accounts with an SPN (Kerberoastable)' }
    @{ Text = '(objectClass=*)';                               Tip = 'Everything under the search base' }
)

$script:LdapFilterCompleter = {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    $script:LdapFilterCompletions | Where-Object { $_.Text -like "*$wordToComplete*" } | ForEach-Object {
        [System.Management.Automation.CompletionResult]::new("'$($_.Text)'", $_.Text, 'ParameterValue', $_.Tip)
    }
}

# Naming attributes explicitly is the biggest single performance lever in an LDAP sweep:
# without it the DC serialises every populated attribute on every entry.
$script:LdapPropertyCompletions = @(
    'sAMAccountName', 'distinguishedName', 'objectSid', 'objectGUID', 'userAccountControl'
    'displayName', 'givenName', 'sn', 'mail', 'userPrincipalName', 'manager', 'department'
    'title', 'whenCreated', 'whenChanged', 'lastLogonTimestamp', 'pwdLastSet', 'logonCount'
    'accountExpires', 'memberOf', 'member', 'primaryGroupID', 'primaryGroupToken', 'groupType'
    'description', 'managedBy', 'adminCount', 'servicePrincipalName', 'operatingSystem'
)

$script:LdapPropertyCompleter = {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    $script:LdapPropertyCompletions | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
    }
}

foreach ($cmd in 'Search-ADxObject') {
    Register-ArgumentCompleter -CommandName $cmd -ParameterName LdapFilter -ScriptBlock $script:LdapFilterCompleter
    Register-ArgumentCompleter -CommandName $cmd -ParameterName Property   -ScriptBlock $script:LdapPropertyCompleter
}

# The presets take RSAT-style property names (and LDAP names) in -Properties.
foreach ($cmd in 'Get-ADxUser', 'Get-ADxGroup', 'Get-ADxComputer', 'Get-ADxObject', 'Get-ADxOrganizationalUnit',
    'Get-ADxServiceAccount', 'Get-ADxFineGrainedPasswordPolicy') {
    Register-ArgumentCompleter -CommandName $cmd -ParameterName LDAPFilter -ScriptBlock $script:LdapFilterCompleter
    Register-ArgumentCompleter -CommandName $cmd -ParameterName Properties -ScriptBlock $script:LdapPropertyCompleter
}

# The membership cmdlets have -Properties but no -LDAPFilter (the filter is derived from the
# resolved principal).
foreach ($cmd in 'Get-ADxGroupMember', 'Get-ADxGroupNested', 'Get-ADxPrincipalGroupMembership') {
    Register-ArgumentCompleter -CommandName $cmd -ParameterName Properties -ScriptBlock $script:LdapPropertyCompleter
}
