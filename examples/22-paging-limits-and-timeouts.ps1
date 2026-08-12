# Result limits, page sizes and timeouts - and which knob does what.
#
# Two cmdlet families with deliberately different defaults, because they answer to
# different masters:
#
#   Get-ADxUser / Group / Computer / Object   -ResultSetSize 0 (UNLIMITED), to match RSAT
#   Search-ADxObject                          stops after ONE page, and warns that it did
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- Result set size ---
# A cap, applied as results stream. ADx also shrinks the wire page to match, so
# -ResultSetSize 1 fetches one entry rather than a full 1,000-entry page it discards.
# Measured on a live DC: 31ms with the page capped, 82ms without.
Get-ADxUser -Filter * -ResultSetSize 5 | Select-Object -ExpandProperty SamAccountName

# --- Page size ---
# Entries per round trip. 1,000 is AD's MaxPageSize default; asking for more does not
# get you more. Lower it only if the DC is enforcing a smaller limit or you want
# smoother progress reporting - it costs round trips.
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$all = @(Get-ADxUser -Filter * -ResultPageSize 1000 -Properties Department).Count
$fast = $sw.ElapsedMilliseconds

$sw.Restart()
$sameCount = @(Get-ADxUser -Filter * -ResultPageSize 100 -Properties Department).Count
$slow = $sw.ElapsedMilliseconds

[PSCustomObject]@{
    Users            = $all
    'PageSize 1000'  = "${fast}ms"
    'PageSize 100'   = "${slow}ms"
    SameResult       = $all -eq $sameCount
} | Format-List

# --- Search-ADxObject: the opposite default ---
# Without -All or -Top it returns one page and tells you so, rather than pretending
# that page was the whole answer.
Search-ADxObject '(objectCategory=person)' -Property sAMAccountName | Measure-Object | Select-Object Count
Search-ADxObject '(objectCategory=person)' -Property sAMAccountName -Top 10 | Measure-Object | Select-Object Count
Search-ADxObject '(objectCategory=person)' -Property sAMAccountName -All | Measure-Object | Select-Object Count

# --- Timeouts ---
# 110 seconds by default, just under AD's MaxQueryDuration of 120, so the client gives
# up marginally before the server does - which produces a clean client-side error
# instead of an opaque server abort. Raise it only if the DC's limit was raised too.
Get-ADxUser -Filter * -Properties '*' -SearchTimeout 300 -ResultSetSize 100 | Measure-Object |
    Select-Object Count

<#
Sample output

Administrator
jdoe
iberg
aruiz
tfisher

Users           : 3732
PageSize 1000   : 410ms
PageSize 100    : 1104ms
SameResult      : True

Count
-----
 1000

WARNING: Search stopped at 1000 entries (one page). Use -All to return everything, or
-Top N for an explicit limit.

Count
-----
   10

Count
-----
 3732

Count
-----
  100

Page size is the one people tune first and should tune last. Ten times the round trips
for the same 3,732 rows cost roughly 2.7x the wall clock here, and returned identical
results. Leave it at 1000 unless the DC forces otherwise.

The warning on the one-page search is the important part of this example. Search-
ADxObject is the raw primitive: it will not silently decide that the first thousand
rows were what you meant. The presets take the opposite default because RSAT does, and
ported scripts would break otherwise.
#>
