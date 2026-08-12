# Export the whole directory to CSV without the memory blowing up.
#
# This is the single most important example in the folder, and it is about one line of
# PowerShell rather than anything ADx does. ADx streams: the engine holds one page at a
# time, never the whole result set. But the way you CALL it decides whether that helps.
#
#   BOUNDED   Get-ADxUser -Filter * | Export-Csv users.csv
#   UNBOUNDED $users = Get-ADxUser -Filter *
#
# The second tells PowerShell to materialise an array of every result - roughly 3 KB per
# object, held by PowerShell, not by ADx. No amount of streaming inside the cmdlet can
# fix it. Measured: 10,000 users cost 8.9 MB streamed against 32.1 MB collected, and
# that second number extrapolates to about 1 GB on a 350,000-user domain.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$outFile = './users.csv'

# Naming the properties is the other half of the performance story. Without -Properties
# the DC sends the default set; with -Properties * it serialises every populated
# attribute on every entry. Ask for exactly what the report needs.
Get-ADxUser -Filter * -Properties EmailAddress, Department, Title, LastLogonDate, whenCreated |
    Select-Object SamAccountName, Name, EmailAddress, Department, Title,
                  Enabled, LastLogonDate, whenCreated, DistinguishedName |
    Export-Csv $outFile -NoTypeInformation -Encoding utf8

"Wrote $outFile ($([math]::Round((Get-Item $outFile).Length / 1MB, 2)) MB)"

# Same shape, one page at a time, when the transform is more than a projection.
# ForEach-Object keeps the pipeline streaming; a foreach() loop over the cmdlet call
# does not, because the parenthesised call is collected first.
$perDepartment = @{}
Get-ADxUser -Filter 'Enabled -eq $true' -Properties Department | ForEach-Object {
    $dept = if ($_.Department) { $_.Department } else { '(none)' }
    $perDepartment[$dept] = 1 + ($perDepartment[$dept] ?? 0)
}

$perDepartment.GetEnumerator() | Sort-Object Value -Descending |
    Select-Object @{N='Department';E={$_.Key}}, @{N='Users';E={$_.Value}} -First 10 |
    Format-Table -AutoSize

<#
Sample output

Wrote ./users.csv (1.24 MB)

Department    Users
----------    -----
Sales          812
Engineering    644
Support        391
Logistics      288
Finance        150
(none)          97

And the reason this example exists, measured client-side on the lab domain:

  Rows     Streamed   Collected into a variable
  1,000    4.5 MB     10.1 MB
  5,000    6.3 MB     18.0 MB
  10,000   8.9 MB     32.1 MB

Streaming is close to flat - ten times the rows costs about twice the memory.
Collecting is linear. Pipe into whatever consumes the data and the problem disappears.
#>
