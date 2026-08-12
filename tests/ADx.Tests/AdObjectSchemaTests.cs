using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M4: the per-type schema rows and their derived fetch lists. The expected attribute lists
/// are the plan's own tables, verbatim -- "dedup after mapping" is the property under test
/// (Enabled/SID/GroupScope collapse onto shared attributes).
/// </summary>
public class AdObjectSchemaTests
{
    [Fact]
    public void GroupDefaults_FetchList_MatchesThePlanTable()
    {
        var list = AdRsatProjector.BuildFetchList(AdObjectSchema.Group, null, false, out _);

        Assert.Equal(
            new[]
            {
                "distinguishedName", "groupType", "name", "objectClass", "objectGUID",
                "sAMAccountName", "objectSid"
            },
            list);
    }

    [Fact]
    public void ComputerDefaults_FetchList_MatchesThePlanTable()
    {
        var list = AdRsatProjector.BuildFetchList(AdObjectSchema.Computer, null, false, out _);

        Assert.Equal(
            new[]
            {
                "distinguishedName", "dNSHostName", "userAccountControl", "name", "objectClass",
                "objectGUID", "sAMAccountName", "objectSid", "userPrincipalName"
            },
            list);
    }

    [Fact]
    public void ObjectDefaults_FetchList_MatchesThePlanTable()
    {
        var list = AdRsatProjector.BuildFetchList(AdObjectSchema.AnyObject, null, false, out _);

        Assert.Equal(new[] { "distinguishedName", "name", "objectClass", "objectGUID" }, list);
    }

    [Fact]
    public void OrganizationalUnitDefaults_FetchList_MatchesThePlanTable()
    {
        var list = AdRsatProjector.BuildFetchList(AdObjectSchema.OrganizationalUnit, null, false, out _);

        // In DefaultProperties order after mapping: City->l, Country->c, StreetAddress->street
        // (the per-schema override, NOT streetAddress), LinkedGroupPolicyObjects->gPLink.
        Assert.Equal(
            new[]
            {
                "l", "c", "distinguishedName", "gPLink", "managedBy", "name", "objectClass",
                "objectGUID", "postalCode", "st", "street"
            },
            list);
    }

    [Fact]
    public void BaseFilters_MatchThePlan()
    {
        // objectCategory=person AND objectClass=user: objectCategory=user alone would also
        // match computers, whose class derives from user.
        Assert.Equal("(&(objectCategory=person)(objectClass=user))", AdObjectSchema.User.BaseFilter);
        Assert.Equal("(objectCategory=group)", AdObjectSchema.Group.BaseFilter);
        Assert.Equal("(objectCategory=computer)", AdObjectSchema.Computer.BaseFilter);
        Assert.Equal("(objectCategory=organizationalUnit)", AdObjectSchema.OrganizationalUnit.BaseFilter);
        Assert.Null(AdObjectSchema.AnyObject.BaseFilter);
    }

    [Fact]
    public void OrganizationalUnit_IdentityIsDnOrGuidOnly_WithStreetOverride()
    {
        Assert.False(AdObjectSchema.OrganizationalUnit.IdentityIncludesSamAccountName);
        Assert.False(AdObjectSchema.OrganizationalUnit.IdentitySamTriesDollarSuffix);
        Assert.Equal("organizationalUnit", AdObjectSchema.OrganizationalUnit.RequiredClass);

        Assert.NotNull(AdObjectSchema.OrganizationalUnit.AttributeOverrides);
        Assert.Equal("street", AdObjectSchema.OrganizationalUnit.AttributeOverrides!["StreetAddress"]);

        // Every other preset leaves the override map null, so user projection is untouched.
        Assert.Null(AdObjectSchema.User.AttributeOverrides);
        Assert.Null(AdObjectSchema.Group.AttributeOverrides);
        Assert.Null(AdObjectSchema.Computer.AttributeOverrides);
        Assert.Null(AdObjectSchema.AnyObject.AttributeOverrides);
    }

    [Fact]
    public void IdentityForms_MatchRsat()
    {
        Assert.True(AdObjectSchema.User.IdentityIncludesSamAccountName);
        Assert.True(AdObjectSchema.Group.IdentityIncludesSamAccountName);
        Assert.True(AdObjectSchema.Computer.IdentityIncludesSamAccountName);
        Assert.False(AdObjectSchema.AnyObject.IdentityIncludesSamAccountName);

        Assert.True(AdObjectSchema.Computer.IdentitySamTriesDollarSuffix);
        Assert.False(AdObjectSchema.User.IdentitySamTriesDollarSuffix);
    }

    // ---- computer projection ----

    private static LdapEntry Entry(string dn, params (string Name, object[] Values)[] attributes)
    {
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in attributes) dict[name] = values;
        return new LdapEntry(dn, dict);
    }

    private static object? Value(PSObject pso, string name) => pso.Properties[name]?.Value;

    [Fact]
    public void ComputerProjection_EmitsDnsHostNameAndEnabled()
    {
        var computer = Entry("CN=WS01,OU=Workstations,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user", "computer" }),
            ("name", new object[] { "WS01" }),
            ("sAMAccountName", new object[] { "WS01$" }),
            ("dNSHostName", new object[] { "ws01.corp.com" }),
            ("userAccountControl", new object[] { "4096" })); // WORKSTATION_TRUST_ACCOUNT

        var pso = AdRsatProjector.Project(computer, AdObjectSchema.Computer, null, false);

        Assert.Equal("ADx.Computer", pso.TypeNames[0]);
        Assert.Equal("ws01.corp.com", Value(pso, "DNSHostName"));
        Assert.Equal("WS01$", Value(pso, "SamAccountName"));
        Assert.Equal(true, Value(pso, "Enabled"));
        // the most specific class for a computer is 'computer', even though the chain
        // includes 'user' -- this is exactly why identity verification uses the LAST element
        Assert.Equal("computer", Value(pso, "ObjectClass"));
    }

    // ---- object-class matching for the -Identity DN fast path ----

    [Fact]
    public void MatchesType_AcceptsDerivedClasses()
    {
        // inetOrgPerson derives from user; its chain therefore ENDS in inetOrgPerson. The
        // wire filter (objectClass=user) matches it, so the DN fast path must too -- the
        // alternative is one cmdlet answering two different ways depending on whether the
        // caller passed a DN or a filter.
        Assert.True(AdObjectSchema.User.MatchesType(
            new[] { "top", "person", "organizationalPerson", "user", "inetOrgPerson" }));

        Assert.True(AdObjectSchema.User.MatchesType(
            new[] { "top", "person", "organizationalPerson", "user" }));
    }

    [Fact]
    public void MatchesType_StillRejectsComputersForUser()
    {
        // The computer class derives from user, which is exactly why widening to "anywhere in
        // the chain" needs the disqualifier: objectCategory=person does this job on the wire.
        Assert.False(AdObjectSchema.User.MatchesType(
            new[] { "top", "person", "organizationalPerson", "user", "computer" }));

        Assert.True(AdObjectSchema.Computer.MatchesType(
            new[] { "top", "person", "organizationalPerson", "user", "computer" }));
    }

    [Fact]
    public void MatchesType_RejectsAnUnrelatedType()
    {
        Assert.False(AdObjectSchema.User.MatchesType(new[] { "top", "group" }));
        Assert.False(AdObjectSchema.Group.MatchesType(
            new[] { "top", "person", "organizationalPerson", "user" }));
    }

    [Fact]
    public void MatchesType_RejectsManagedServiceAccountsForComputer()
    {
        // gMSA/sMSA derive from computer, but their objectCategory is their own class, so
        // Get-ADxComputer's wire filter excludes them. The DN fast path must agree -- and
        // they must not sneak into Get-ADxUser through the user class in their chain either.
        var gmsa = new[]
        {
            "top", "person", "organizationalPerson", "user", "computer",
            "msDS-GroupManagedServiceAccount"
        };
        var smsa = new[]
        {
            "top", "person", "organizationalPerson", "user", "computer",
            "msDS-ManagedServiceAccount"
        };

        Assert.False(AdObjectSchema.Computer.MatchesType(gmsa));
        Assert.False(AdObjectSchema.Computer.MatchesType(smsa));
        Assert.False(AdObjectSchema.User.MatchesType(gmsa));

        // A plain computer still resolves through Get-ADxComputer.
        Assert.True(AdObjectSchema.Computer.MatchesType(
            new[] { "top", "person", "organizationalPerson", "user", "computer" }));
    }

    [Fact]
    public void MatchesType_WithNoRequiredClass_AcceptsAnything()
    {
        Assert.True(AdObjectSchema.AnyObject.MatchesType(new[] { "top", "organizationalUnit" }));
        Assert.True(AdObjectSchema.AnyObject.MatchesType(Array.Empty<string>()));
    }

    [Fact]
    public void MatchesType_EmptyChain_IsNotAMatch()
    {
        Assert.False(AdObjectSchema.User.MatchesType(Array.Empty<string>()));
    }

    [Fact]
    public void ObjectProjection_IsMinimal()
    {
        var ou = Entry("OU=Sales,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "organizationalUnit" }),
            ("name", new object[] { "Sales" }),
            ("objectGUID", new object[] { Guid.Parse("01234567-89ab-cdef-0123-456789abcdef").ToByteArray() }));

        var pso = AdRsatProjector.Project(ou, AdObjectSchema.AnyObject, null, false);

        Assert.Equal("ADx.Object", pso.TypeNames[0]);
        Assert.Equal("organizationalUnit", Value(pso, "ObjectClass"));
        Assert.Equal("Sales", Value(pso, "Name"));
        Assert.Equal(4, pso.Properties.Count());
    }
}
