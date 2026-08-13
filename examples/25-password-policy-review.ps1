# What the domain actually demands of a password - and who gets a different answer.
#
# Password policy has two sources, stored in two different places:
#
#   1. The DEFAULT domain policy is attribute data on the domain head itself -
#      maxPwdAge, lockoutThreshold and friends, directly on DC=corp,DC=contoso,DC=com.
#      One base-scope read. It applies to every account that nothing overrides.
#
#   2. Fine-grained policies (PSOs) are separate msDS-PasswordSettings objects in
#      CN=Password Settings Container,CN=System. Each names the users and GLOBAL
#      SECURITY groups it applies to; where several reach the same user, the lowest
#      Precedence value wins.
#
# The age and duration values are AD *interval* attributes - negative 100-nanosecond
# tick counts, a different animal from the FILETIME timestamps that look identical on
# the wire. ADx decodes them on their own syntax and surfaces positive TimeSpans;
# decoding them as FILETIME would silently null every one. One documented divergence
# from RSAT: the stored "never" sentinel surfaces as [TimeSpan]::MaxValue where RSAT
# collapses it into 00:00:00 - so a never-expire audit must test BOTH 00:00:00 (stored
# "none") and MaxValue (see the README's divergences list). The same syntax reasoning
# is why -Filter refuses interval attributes outright - compare them after reading,
# as the last block does.
#
# Requirements: PowerShell 7.5+, read access to the directory (but see the PSO
# permission note at the bottom).

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- The default domain policy ---
$policy = Get-ADxDefaultDomainPasswordPolicy
$policy

# The TimeSpans compute. A MaxPasswordAge of 00:00:00 is the stored "passwords do not
# expire" value at the domain level; the explicit "never" sentinel surfaces as
# TimeSpan.MaxValue. Neither is a parsing accident, so test for them before doing
# arithmetic that assumes expiry.
"Passwords expire every $([int]$policy.MaxPasswordAge.TotalDays) days; " +
"lockout after $($policy.LockoutThreshold) bad attempts, for $([int]$policy.LockoutDuration.TotalMinutes) minutes."

# --- Every fine-grained policy, in the order they win ---
# The search defaults to the Password Settings Container, so -Filter * is the whole
# PSO population, not a domain sweep.
Get-ADxFineGrainedPasswordPolicy -Filter * |
    Sort-Object Precedence |
    Select-Object Name, Precedence, MinPasswordLength, MaxPasswordAge,
                  LockoutThreshold, ComplexityEnabled |
    Format-Table -AutoSize

# --- Who each policy applies to ---
# AppliesTo is the msDS-PSOAppliesTo forward link: a DN list, always an array. A PSO
# that applies to nobody is dead configuration and shows up here as no rows.
Get-ADxFineGrainedPasswordPolicy -Filter * | ForEach-Object {
    foreach ($dn in $_.AppliesTo) {
        [PSCustomObject]@{ Policy = $_.Name; Precedence = $_.Precedence; AppliesTo = $dn }
    }
} | Sort-Object Precedence | Format-Table -AutoSize

# --- PSOs weaker than the domain floor ---
# The usual point of a PSO is to be STRICTER for admins and service accounts; one
# that is laxer than the default deserves an explanation on file. Interval attributes
# cannot go in -Filter (see the header), so the comparison is client-side by design -
# the PSO population is a handful of objects, not a directory sweep.
Get-ADxFineGrainedPasswordPolicy -Filter * |
    Where-Object {
        $_.MinPasswordLength -lt $policy.MinPasswordLength -or
        ($policy.MaxPasswordAge -gt [TimeSpan]::Zero -and $_.MaxPasswordAge -gt $policy.MaxPasswordAge)
    } |
    Select-Object Name, Precedence, MinPasswordLength, MaxPasswordAge |
    Format-Table -AutoSize

<#
Sample output

ComplexityEnabled           : True
DistinguishedName           : DC=corp,DC=contoso,DC=com
LockoutDuration             : 00:30:00
LockoutObservationWindow    : 00:30:00
LockoutThreshold            : 5
MaxPasswordAge              : 42.00:00:00
MinPasswordAge              : 1.00:00:00
MinPasswordLength           : 8
PasswordHistoryCount        : 24
ReversibleEncryptionEnabled : False

Passwords expire every 42 days; lockout after 5 bad attempts, for 30 minutes.

Name                 Precedence MinPasswordLength MaxPasswordAge LockoutThreshold ComplexityEnabled
----                 ---------- ----------------- -------------- ---------------- -----------------
PSO-Tier0-Admins             10                20    30.00:00:00                3              True
PSO-ServiceAccounts          50                32   365.00:00:00                5              True
PSO-Kiosks                  200                 6    90.00:00:00               10             False

Policy              Precedence AppliesTo
------              ---------- ---------
PSO-Tier0-Admins            10 CN=SG-Tier0,OU=Groups,DC=corp,DC=contoso,DC=com
PSO-ServiceAccounts         50 CN=SG-ServiceAccounts,OU=Groups,DC=corp,DC=contoso,DC=com
PSO-Kiosks                 200 CN=SG-Kiosks,OU=Groups,DC=corp,DC=contoso,DC=com

Name                Precedence MinPasswordLength MaxPasswordAge
----                ---------- ----------------- --------------
PSO-ServiceAccounts         50                32   365.00:00:00
PSO-Kiosks                 200                 6    90.00:00:00

The last table is a conversation list, not a verdict. PSO-ServiceAccounts trades a
365-day age for a 32-character minimum - defensible, and now documented. PSO-Kiosks
grants 6-character non-complex passwords to whatever SG-Kiosks contains; the follow-up
is Get-ADxGroupMember SG-Kiosks -Recursive (example 12), because the policy is only as
narrow as that group's effective membership.

How a user's winner is chosen when several PSOs reach them: a PSO applied DIRECTLY to
the user beats any group-applied PSO regardless of numbers; among the rest the lowest
Precedence wins; a dead tie breaks on lowest objectGUID. The DC computes the final
answer per user in the constructed msDS-ResultantPSO attribute. And PSOs attach only
to users and GLOBAL security groups - applying one to a domain-local group, or hoping
it reaches computer accounts, does nothing.

The permission gotcha: by default only Domain Admins can read the contents of the
Password Settings Container. Everyone else gets ZERO ROWS and a success code from
-Filter * - not an error, and the same from RSAT, because the ACL is on the container.
An empty PSO table is therefore not evidence that no PSOs exist; confirm with
credentials that can see them before writing "none" in a report.
#>
