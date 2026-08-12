# What changed in the directory this week, regardless of object class?
#
# Get-ADxObject is the untyped preset - RSAT's Get-ADObject. It applies no object-class
# filter of its own, so "-Filter *" really does mean everything under the search base.
# That makes it the right cmdlet for change tracking, where you do not know in advance
# whether the answer is a user, a group, a GPO or an OU.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$since = (Get-Date).AddDays(-7)

# whenChanged is GeneralizedTime, so $since is marshalled to 20260804113406.0Z on the
# wire. Single quotes keep it a DateTime until the translator sees it.
#
# This one deliberately collects into a variable, against the advice in example 05,
# because a week of changes is a small bounded set and three reports read it. Widen
# the window far enough and that stops being true - then stream it instead.
$changed = Get-ADxObject -Filter 'whenChanged -ge $since' -Properties whenChanged, whenCreated

$changed | Group-Object ObjectClass |
    Select-Object @{N='ObjectClass'; E={$_.Name}}, Count |
    Sort-Object Count -Descending | Format-Table -AutoSize

# New objects versus modified ones - whenCreated inside the window means new.
$changed |
    Select-Object Name, ObjectClass, whenCreated, whenChanged,
                  @{N='State'; E={ if ($_.whenCreated -ge $since) { 'created' } else { 'modified' } }} |
    Sort-Object whenChanged -Descending |
    Select-Object -First 15 | Format-Table -AutoSize

# Objects created in the window, by class, is the useful alerting signal.
$changed | Where-Object { $_.whenCreated -ge $since } |
    Select-Object Name, ObjectClass, whenCreated, DistinguishedName |
    Sort-Object whenCreated | Format-Table -AutoSize

<#
Sample output

ObjectClass          Count
-----------          -----
user                   146
computer                88
group                   12
organizationalUnit       2
groupPolicyContainer     1

Name          ObjectClass          whenCreated         whenChanged         State
----          -----------          -----------         -----------         -----
WS-4417       computer             2026-08-11 09:12:03 2026-08-11 09:12:44 created
jdoe          user                 2023-04-19 10:22:51 2026-08-11 08:41:19 modified
SG-Contractors group               2026-08-09 15:30:02 2026-08-10 11:05:37 created
Default Domain Policy groupPolicyContainer 2019-01-14 12:00:00 2026-08-08 16:22:41 modified

Name           ObjectClass          whenCreated         DistinguishedName
----           -----------          -----------         -----------------
SG-Contractors group                2026-08-09 15:30:02 CN=SG-Contractors,OU=Groups,DC=corp,...
WS-4417        computer             2026-08-11 09:12:03 CN=WS-4417,OU=Workstations,DC=corp,...

Two things this is not:

  It is not an audit log. whenChanged is a timestamp, not a history - it tells you an
  object changed, never what changed or who changed it. For that you need the Security
  event log on the DC, or a DirSync-based collector.

  It is not replication-safe on its own. whenChanged is per-DC and not replicated as a
  value, so two DCs can disagree by seconds to minutes. Pin -Server to one DC for a
  report you intend to compare against itself over time.

If you want real incremental sync, check that the DC advertises the DirSync control
(1.2.840.113556.1.4.841) - example 01 shows how - and use uSNChanged watermarking.
#>
