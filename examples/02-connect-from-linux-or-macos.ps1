# Query Active Directory from a machine that has never heard of RSAT.
#
# This is the case ADx exists for. The ActiveDirectory module cannot be installed on
# Linux or macOS at all, so "faster" is beside the point - it is the difference
# between possible and impossible. What follows is the authentication decision tree,
# because that is the only part that is genuinely different off Windows.
#
# Requirements: PowerShell 7.5+, TCP reach to a DC. No domain join, no RSAT, no ADWS.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$dc = 'dc1.corp.contoso.com'

# --- Windows, domain-joined: nothing to configure. Kerberos via SSPI. ---
if ($IsWindows) {
    Get-ADxUser -Filter * -ResultSetSize 5
    return
}

# --- Linux / macOS, option A: an existing Kerberos ticket ---
# kinit jdoe@CORP.CONTOSO.COM     (run in a shell first, then omit -Credential)
# The ticket is used implicitly; this is the only option that keeps mutual auth
# and needs no LDAPS certificate on the DC.
try {
    Get-ADxUser -Filter * -Server $dc -ResultSetSize 5 -ErrorAction Stop
    return
} catch {
    Write-Host "No usable ticket: $($_.Exception.Message.Split([Environment]::NewLine)[0])"
}

# --- Linux / macOS, option B: explicit credentials over LDAPS ---
# The LDAP client library on non-Windows platforms cannot perform a SASL/GSSAPI bind
# from a username and password - only Windows can, because it brokers through SSPI.
# So -Credential requires a simple bind, which puts the password on the wire, which
# means -UseSsl is not optional. ADx warns if you try it without.
$cred = Get-Credential -Message 'CORP\jdoe'
Get-ADxUser -Filter * -Server $dc -UseSsl -AuthType Basic -Credential $cred -ResultSetSize 5

<#
Sample output

On macOS with no ticket and the default -AuthType Negotiate, the first attempt fails
with the cause and both ways out named explicitly, rather than "The feature is not
supported":

No usable ticket: -AuthType Negotiate with -Credential is not supported by the LDAP
client library on Linux and macOS (only Windows can broker Negotiate/Kerberos through
SSPI). Either use '-AuthType Basic -UseSsl' (a simple bind, encrypted by LDAPS -- never
Basic without -UseSsl, which sends the password in cleartext), or obtain a Kerberos
ticket with 'kinit user@REALM' and omit -Credential so the existing ticket is used.

Then, over LDAPS on 636:

DistinguishedName : CN=Jane Doe,OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com
Enabled           : True
GivenName         : Jane
Name              : Jane Doe
ObjectClass       : user
ObjectGUID        : 8f2b1c4e-6a19-4d33-9f07-2c5b1e0a77d4
SamAccountName    : jdoe
SID               : S-1-5-21-1004336348-1177238915-682003330-1163
Surname           : Doe
UserPrincipalName : jdoe@corp.contoso.com

...4 more.

Two warnings worth understanding rather than suppressing:

  WARNING: LDAP signing/sealing is a Windows-only session option, so this connection
  is unsigned and unencrypted. Use -UseSsl (LDAPS, port 636) if the traffic crosses
  an untrusted network.

  Fires on plain 389 from Linux/macOS. Harmless on a trusted segment, not harmless
  across a WAN.

  WARNING: -AuthType Basic without -UseSsl sends the password in CLEARTEXT over the
  network. Use -UseSsl (LDAPS, port 636) or -AuthType Negotiate/Kerberos instead.

  Fires only if you use Basic WITHOUT -UseSsl. Do not silence this one; fix it.
#>
