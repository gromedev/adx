# Map the territory: domain, forest, domain controllers, and the OU tree.
#
# The first questions against any unfamiliar environment: what domain is this, where
# does it sit in its forest, which DCs serve it and from which sites, and how is the
# directory organised? RSAT answers them through ADWS plus the netlogon DC locator;
# ADx answers all of them over plain LDAP, because every answer already sits in the
# directory - on the domain head and in the configuration partition, which is
# replicated to every DC in the forest.
#
# One targeting difference to absorb up front: there is no -Identity on Get-ADxDomain
# or Get-ADxForest, and no -Discover on Get-ADxDomainController. RSAT locates domains
# and DCs with the netlogon/CLDAP locator protocol, which is not LDAP, so ADx declares
# those unsupported instead of approximating them. The whole targeting model is: point
# -Server at a DC (or the DNS name) of the domain you want.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- The domain: identity, functional level, and its three FSMO roles ---
# One call joins the domain head with the configuration partition. Role owners are
# stored as nTDSDSA DNs; ADx resolves each to the DC's dNSHostName, so PDCEmulator
# and friends come back as hostnames, exactly as RSAT emits them.
$domain = Get-ADxDomain
$domain

# --- The forest: root, functional level, and the other two FSMO roles ---
# Any DC can answer this - the configuration partition is forest-wide - so you never
# need to reach the forest root domain to ask about it.
$forest = Get-ADxForest
$forest | Select-Object RootDomain, ForestMode, Domains, GlobalCatalogs, Sites | Format-List

# All five FSMO roles in one table. Three are per-domain, two are forest-wide - which
# is why they come from two different cmdlets.
[PSCustomObject]@{
    PDCEmulator          = $domain.PDCEmulator
    RIDMaster            = $domain.RIDMaster
    InfrastructureMaster = $domain.InfrastructureMaster
    SchemaMaster         = $forest.SchemaMaster
    DomainNamingMaster   = $forest.DomainNamingMaster
} | Format-List

# --- Every DC in the domain ---
# -Filter accepts only * here (a property filter over the joined DC shape is declared
# out of scope): enumerate everything, filter in PowerShell. Each DC is a join of its
# nTDSDSA and server objects in the configuration partition with its computer account
# in the domain partition.
Get-ADxDomainController -Filter * |
    Select-Object Name, Site, IsGlobalCatalog, IsReadOnly, OperatingSystem,
                  @{N='FSMORoles'; E={ $_.OperationMasterRoles -join ', ' }} |
    Format-Table -AutoSize

# Global catalogs specifically: the DCs that answer forest-wide queries on 3268. The
# moment a forest has a second domain, these are the servers example 28 cares about.
Get-ADxDomainController -Filter * | Where-Object IsGlobalCatalog |
    Select-Object HostName, Site | Format-Table -AutoSize

# --- The OU tree, with its GPO links ---
# An OU's place in the tree IS its distinguished name, so sorting by the reversed DN
# yields tree order and the component count yields depth. LinkedGroupPolicyObjects is
# parsed from each OU's gPLink in link order - enforced and disabled links included,
# matching RSAT - and it is always an array, so .Count is always valid.
$domainDepth = ($domain.DistinguishedName -split ',').Count
Get-ADxOrganizationalUnit -Filter * |
    Sort-Object { $rdns = $_.DistinguishedName -split ','; [array]::Reverse($rdns); $rdns -join ',' } |
    ForEach-Object {
        [PSCustomObject]@{
            OU        = ('  ' * (($_.DistinguishedName -split ',').Count - $domainDepth - 1)) + $_.Name
            GPOLinks  = $_.LinkedGroupPolicyObjects.Count
            ManagedBy = $_.ManagedBy
        }
    } | Format-Table -AutoSize

# OUs with no GPO links at all: either deliberately inheriting everything from above,
# or forgotten. Worth knowing which one it is.
Get-ADxOrganizationalUnit -Filter * |
    Where-Object { $_.LinkedGroupPolicyObjects.Count -eq 0 } |
    Select-Object Name, DistinguishedName | Format-Table -AutoSize

<#
Sample output

DistinguishedName               : DC=corp,DC=contoso,DC=com
Name                            : corp
DNSRoot                         : corp.contoso.com
NetBIOSName                     : CORP
DomainMode                      : Windows2016Domain
DomainSID                       : S-1-5-21-3623811015-3361044348-30300820
Forest                          : corp.contoso.com
ParentDomain                    :
ChildDomains                    : {}
PDCEmulator                     : dc1.corp.contoso.com
RIDMaster                       : dc1.corp.contoso.com
InfrastructureMaster            : dc2.corp.contoso.com
ReplicaDirectoryServers         : {dc1.corp.contoso.com, dc2.corp.contoso.com}
ReadOnlyReplicaDirectoryServers : {}

RootDomain     : corp.contoso.com
ForestMode     : Windows2016Forest
Domains        : {corp.contoso.com}
GlobalCatalogs : {dc1.corp.contoso.com, dc2.corp.contoso.com}
Sites          : {Default-First-Site-Name, Branch-Oslo}

PDCEmulator          : dc1.corp.contoso.com
RIDMaster            : dc1.corp.contoso.com
InfrastructureMaster : dc2.corp.contoso.com
SchemaMaster         : dc1.corp.contoso.com
DomainNamingMaster   : dc1.corp.contoso.com

Name Site                    IsGlobalCatalog IsReadOnly OperatingSystem              FSMORoles
---- ----                    --------------- ---------- ---------------              ---------
DC1  Default-First-Site-Name            True      False Windows Server 2022 Standard PDCEmulator, RIDMaster, SchemaMaster, DomainNamingMaster
DC2  Branch-Oslo                        True      False Windows Server 2022 Standard InfrastructureMaster

HostName             Site
--------             ----
dc1.corp.contoso.com Default-First-Site-Name
dc2.corp.contoso.com Branch-Oslo

OU                   GPOLinks ManagedBy
--                   -------- ---------
Domain Controllers          1
Engineering                 2 CN=Ida Berg,OU=Users,OU=Engineering,DC=corp,DC=contoso,DC=com
  Users                     0
Finance                     1
  Users                     0
Groups                      0
Kiosks                      1
Sales                       1
  Users                     0
Servers                     2
  Lab                       1
Support                     1
  Users                     0
Workstations                1

Name   DistinguishedName
----   -----------------
Users  OU=Users,OU=Engineering,DC=corp,DC=contoso,DC=com
Users  OU=Users,OU=Finance,DC=corp,DC=contoso,DC=com
Groups OU=Groups,DC=corp,DC=contoso,DC=com
Users  OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com
Users  OU=Users,OU=Support,DC=corp,DC=contoso,DC=com

DomainMode Windows2016Domain is the same behavior-version 7 that example 01 showed as
DomainControllerFunctionality - one decoded for the domain, one for the DC.

Two things worth reading out of the tables rather than filing away:

  InfrastructureMaster placement only matters in a multi-domain forest, and then it
  matters a lot: the role must NOT sit on a global catalog unless every DC is one,
  or cross-domain phantom cleanup silently stops. Here every DC is a GC, so the
  placement above is fine - but this table is where you would catch it.

  The OU tree is not the whole directory. CN=Users and CN=Computers - where accounts
  land by default - are CONTAINERS, not OUs, so they are absent above and can carry
  no GPO links. An account that was never moved out of them is beyond the reach of
  every OU-linked GPO in this table, which is worth checking before concluding a
  policy "applies to everyone".
#>
