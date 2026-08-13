using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M3: the RSAT-fidelity output projector and its fetch-list builder. All offline: entries
/// are constructed by hand, exactly as a DC would have returned them.
/// </summary>
public class AdRsatProjectorTests
{
    private static LdapEntry Entry(string dn, params (string Name, object[] Values)[] attributes)
    {
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in attributes) dict[name] = values;
        return new LdapEntry(dn, dict);
    }

    private static PSObject ProjectUser(LdapEntry entry, string[]? properties = null, bool fetchAll = false) =>
        AdRsatProjector.Project(entry, AdObjectSchema.User, properties, fetchAll);

    private static object? Value(PSObject pso, string name) => pso.Properties[name]?.Value;

    private static LdapEntry TypicalUser() => Entry(
        "CN=John Doe,OU=Users,DC=corp,DC=com",
        ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
        ("name", new object[] { "John Doe" }),
        ("givenName", new object[] { "John" }),
        ("sn", new object[] { "Doe" }),
        ("sAMAccountName", new object[] { "jdoe" }),
        ("userPrincipalName", new object[] { "jdoe@corp.com" }),
        ("userAccountControl", new object[] { "512" }),
        ("objectGUID", new object[] { Guid.Parse("01234567-89ab-cdef-0123-456789abcdef").ToByteArray() }),
        ("objectSid", new object[] { LdapConvert.SddlToSid("S-1-5-21-1-2-3-1013")! }));

    // ---- fetch list: the plan's own table is the golden expectation ----

    [Fact]
    public void UserDefaults_FetchList_MatchesThePlanTable()
    {
        var list = AdRsatProjector.BuildFetchList(AdObjectSchema.User, null, false, out var fetchAll);

        Assert.False(fetchAll);
        // "dedup after mapping -- Enabled/SID collapse onto shared attributes"
        Assert.Equal(
            new[]
            {
                "distinguishedName", "userAccountControl", "givenName", "name", "objectClass",
                "objectGUID", "sAMAccountName", "objectSid", "sn", "userPrincipalName"
            },
            list);
    }

    [Fact]
    public void ExtraProperties_AppendTheirAttributes_Deduplicated()
    {
        var list = AdRsatProjector.BuildFetchList(
            AdObjectSchema.User, new[] { "EmailAddress", "Department", "GivenName" }, false, out _);

        Assert.Contains("mail", list);
        Assert.Contains("department", list);
        // GivenName was already in the defaults; no duplicate
        Assert.Equal(1, list.Count(a => a.Equals("givenName", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SyntheticExtras_FetchTheirSourceAttributes()
    {
        var list = AdRsatProjector.BuildFetchList(
            AdObjectSchema.User, new[] { "LockedOut", "AccountLockoutTime", "PasswordExpired" }, false, out _);

        // LockedOut reads the computed UAC (not lockoutTime -- see the projector);
        // AccountLockoutTime is what still needs lockoutTime.
        Assert.Contains("lockoutTime", list);
        Assert.Contains("msDS-User-Account-Control-Computed", list);
    }

    [Fact]
    public void Star_SendsLiteralStar_PlusConstructedSources()
    {
        var list = AdRsatProjector.BuildFetchList(
            AdObjectSchema.User, new[] { "*", "PasswordExpired" }, false, out var fetchAll);

        Assert.True(fetchAll);
        Assert.Contains("*", list);
        // "*" never returns constructed attributes; they must be named alongside it
        Assert.Contains("msDS-User-Account-Control-Computed", list);
    }

    // ---- property validation ----

    [Fact]
    public void MisspelledProperty_IsATerminatingErrorBeforeAnyQuery()
    {
        var ex = Assert.Throws<AdFilterTranslationException>(() =>
            AdRsatProjector.ValidateRequestedProperties(new[] { "Deparment" }, false));
        Assert.Contains("AllowUnknownProperty", ex.Message);
    }

    [Fact]
    public void UnknownProperty_AllowedWithEscapeHatch()
    {
        AdRsatProjector.ValidateRequestedProperties(new[] { "extensionAttribute7" }, true);
        var list = AdRsatProjector.BuildFetchList(
            AdObjectSchema.User, new[] { "extensionAttribute7" }, true, out _);
        Assert.Contains("extensionAttribute7", list);
    }

    [Theory]
    [InlineData("PrimaryGroup")]
    [InlineData("IPv4Address")]
    [InlineData("ProtectedFromAccidentalDeletion")]
    [InlineData("PrincipalsAllowedToDelegateToAccount")]
    [InlineData("PrincipalsAllowedToRetrieveManagedPassword")]
    public void DeclaredUnsupportedOutputProperties_AreRejectedExplicitly(string name)
    {
        // "Declare unsupported, don't return null": these must error even with the hatch open.
        var ex = Assert.Throws<AdFilterTranslationException>(() =>
            AdRsatProjector.ValidateRequestedProperties(new[] { name }, true));
        Assert.Contains("not supported", ex.Message);
    }

    // ---- default projection ----

    [Fact]
    public void Defaults_EmitRsatNames()
    {
        var pso = ProjectUser(TypicalUser());

        Assert.Equal("CN=John Doe,OU=Users,DC=corp,DC=com", Value(pso, "DistinguishedName"));
        Assert.Equal("John Doe", Value(pso, "Name"));
        Assert.Equal("John", Value(pso, "GivenName"));
        Assert.Equal("Doe", Value(pso, "Surname"));
        Assert.Equal("jdoe", Value(pso, "SamAccountName"));
        Assert.Equal("jdoe@corp.com", Value(pso, "UserPrincipalName"));
    }

    [Fact]
    public void ObjectClass_IsTheMostSpecificClassAsASingleString()
    {
        // $u.ObjectClass -eq 'user' must work; the raw attribute is the full class chain.
        Assert.Equal("user", Value(ProjectUser(TypicalUser()), "ObjectClass"));
    }

    [Fact]
    public void Enabled_DecodesInverted_FromUac()
    {
        Assert.Equal(true, Value(ProjectUser(TypicalUser()), "Enabled"));

        var disabled = Entry("CN=X,DC=x", ("userAccountControl", new object[] { "514" }));
        Assert.Equal(false, Value(ProjectUser(disabled), "Enabled"));
    }

    [Fact]
    public void Sid_IsAnADxSecurityIdentifier_WithWorkingValueProperty()
    {
        // $u.SID.Value is common in real scripts; a bare string breaks it.
        var sid = Assert.IsType<ADxSecurityIdentifier>(Value(ProjectUser(TypicalUser()), "SID"));
        Assert.Equal("S-1-5-21-1-2-3-1013", sid.Value);
        Assert.Equal("S-1-5-21-1-2-3", sid.AccountDomainSid);
    }

    [Fact]
    public void ObjectGuid_IsAGuid()
    {
        Assert.Equal(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            Value(ProjectUser(TypicalUser()), "ObjectGUID"));
    }

    [Fact]
    public void RequestedButAbsent_PropertiesArePresentWithNull()
    {
        // RSAT keeps every requested property on the object even when the DC returned no
        // value -- Select-Object columns depend on it.
        var sparse = Entry("CN=X,DC=x", ("name", new object[] { "X" }));
        var pso = ProjectUser(sparse, new[] { "Department" });

        Assert.NotNull(pso.Properties["Department"]);
        Assert.Null(Value(pso, "Department"));
        Assert.NotNull(pso.Properties["Enabled"]);
        Assert.Null(Value(pso, "Enabled"));
    }

    [Fact]
    public void TypeNames_CarryTheAdxUserBrand()
    {
        var pso = ProjectUser(TypicalUser());
        Assert.Equal("ADx.User", pso.TypeNames[0]);
        Assert.Equal("ADx.Entry", pso.TypeNames[1]);
    }

    // ---- date fidelity: LOCAL DateTime, matching RSAT ----

    [Fact]
    public void GeneralizedTimeDates_AreLocalDateTimes()
    {
        var entry = Entry("CN=X,DC=x", ("whenCreated", new object[] { "20240102030405.0Z" }));
        var pso = ProjectUser(entry, new[] { "Created" });

        var created = Assert.IsType<DateTime>(Value(pso, "Created"));
        Assert.Equal(DateTimeKind.Local, created.Kind);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToLocalTime(), created);
    }

    [Fact]
    public void FileTimeDates_AreLocalDateTimes()
    {
        var utc = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var entry = Entry("CN=X,DC=x",
            ("lastLogonTimestamp", new object[] { utc.ToFileTimeUtc().ToString() }));
        var pso = ProjectUser(entry, new[] { "LastLogonDate" });

        var lastLogon = Assert.IsType<DateTime>(Value(pso, "LastLogonDate"));
        Assert.Equal(DateTimeKind.Local, lastLogon.Kind);
        Assert.Equal(utc.ToLocalTime(), lastLogon);
    }

    [Fact]
    public void NeverSentinels_ProjectAsNull()
    {
        var entry = Entry("CN=X,DC=x",
            ("pwdLastSet", new object[] { "0" }),
            ("accountExpires", new object[] { "9223372036854775807" }));
        var pso = ProjectUser(entry, new[] { "PasswordLastSet", "AccountExpirationDate" });

        Assert.Null(Value(pso, "PasswordLastSet"));
        Assert.Null(Value(pso, "AccountExpirationDate"));
    }

    // ---- synthetics beyond the defaults ----

    [Fact]
    public void LockedOut_ReadsTheComputedUacBit_NotLockoutTime()
    {
        // The DC evaluates ADS_UF_LOCKOUT (0x10) against the lockout window; the stored
        // lockoutTime persists after expiry. lockoutTime rides along here to prove it is
        // IGNORED: a stale lockout (lockoutTime set, bit clear) must read False, matching
        // RSAT -- the old lockoutTime > 0 rule reported it as locked.
        var locked = Entry("CN=X,DC=x",
            ("lockoutTime", new object[] { "133497000000000000" }),
            ("msDS-User-Account-Control-Computed", new object[] { "16" }));
        Assert.Equal(true, Value(ProjectUser(locked, new[] { "LockedOut" }), "LockedOut"));

        var stale = Entry("CN=X,DC=x",
            ("lockoutTime", new object[] { "133497000000000000" }),
            ("msDS-User-Account-Control-Computed", new object[] { "0" }));
        Assert.Equal(false, Value(ProjectUser(stale, new[] { "LockedOut" }), "LockedOut"));

        // No computed attribute at all -> null ("not fetched"), same convention as
        // PasswordExpired, not a guessed False.
        var never = Entry("CN=X,DC=x");
        Assert.Null(Value(ProjectUser(never, new[] { "LockedOut" }), "LockedOut"));
    }

    [Fact]
    public void Description_IsAScalarString_MatchingRsatFlattening()
    {
        // Multi-valued in the AD schema, but RSAT flattens it and scripts Substring/Export-Csv
        // it; an array here rendered as "System.String[]" where RSAT shows the text.
        var entry = Entry("CN=X,DC=x", ("description", new object[] { "Service account for backups" }));
        Assert.Equal(
            "Service account for backups",
            Value(ProjectUser(entry, new[] { "Description" }), "Description"));
    }

    [Fact]
    public void IntegerWidths_FollowTheAdSyntax_Int32ForInteger_Int64ForLargeInteger()
    {
        var entry = Entry("CN=X,DC=x",
            ("logonCount", new object[] { "42" }),
            // A USN comfortably past Int32.MaxValue -- the reason uSN* are LargeInteger.
            ("uSNChanged", new object[] { "5000000000" }));
        var pso = ProjectUser(entry, new[] { "logonCount", "uSNChanged" });

        Assert.Equal(42, Value(pso, "logonCount"));
        Assert.Equal(5_000_000_000L, Value(pso, "uSNChanged"));
    }

    [Fact]
    public void PasswordExpired_ReadsTheComputedUac_NotTheStoredOne()
    {
        // Stored userAccountControl's 0x800000 bit is meaningless; only the constructed
        // msDS-User-Account-Control-Computed carries it.
        var expired = Entry("CN=X,DC=x",
            ("userAccountControl", new object[] { "512" }),
            ("msDS-User-Account-Control-Computed", new object[] { "8388608" }));
        Assert.Equal(true, Value(ProjectUser(expired, new[] { "PasswordExpired" }), "PasswordExpired"));

        var current = Entry("CN=X,DC=x",
            ("msDS-User-Account-Control-Computed", new object[] { "0" }));
        Assert.Equal(false, Value(ProjectUser(current, new[] { "PasswordExpired" }), "PasswordExpired"));
    }

    [Fact]
    public void PasswordNeverExpires_FromUacBit()
    {
        var entry = Entry("CN=X,DC=x", ("userAccountControl", new object[] { "66048" })); // 512 | 0x10000
        Assert.Equal(true, Value(ProjectUser(entry, new[] { "PasswordNeverExpires" }), "PasswordNeverExpires"));
    }

    // ---- aliases: ask in LDAP terms, get both names ----

    [Fact]
    public void LdapAliasRequest_EmitsBothRsatAndLdapNames()
    {
        var entry = Entry("CN=X,DC=x", ("mail", new object[] { "a@b.c" }));
        var pso = ProjectUser(entry, new[] { "mail" });

        Assert.Equal("a@b.c", Value(pso, "EmailAddress"));
        Assert.Equal("a@b.c", Value(pso, "mail"));
    }

    [Fact]
    public void RsatNameRequest_EmitsJustTheRsatName()
    {
        var entry = Entry("CN=X,DC=x", ("mail", new object[] { "a@b.c" }));
        var pso = ProjectUser(entry, new[] { "EmailAddress" });

        Assert.Equal("a@b.c", Value(pso, "EmailAddress"));
        Assert.Null(pso.Properties["mail"]);
    }

    // ---- multi-value shape ----

    [Fact]
    public void MemberOf_IsAlwaysAnArray_EvenWithOneValue()
    {
        var entry = Entry("CN=X,DC=x", ("memberOf", new object[] { "CN=G1,DC=x" }));
        var memberOf = Assert.IsType<string[]>(Value(ProjectUser(entry, new[] { "MemberOf" }), "MemberOf"));
        Assert.Single(memberOf);
    }

    [Fact]
    public void MemberOf_AbsentIsAnEmptyArray()
    {
        var entry = Entry("CN=X,DC=x");
        var memberOf = Assert.IsType<string[]>(Value(ProjectUser(entry, new[] { "MemberOf" }), "MemberOf"));
        Assert.Empty(memberOf);
    }

    [Fact]
    public void SingleValuedStrings_AreScalars()
    {
        var entry = Entry("CN=X,DC=x", ("department", new object[] { "Sales" }));
        Assert.IsType<string>(Value(ProjectUser(entry, new[] { "Department" }), "Department"));
    }

    // ---- -Properties * ----

    [Fact]
    public void FetchAll_EmitsDefaultsAndRawAttributesWithoutCaseCollisions()
    {
        var pso = ProjectUser(TypicalUser(), new[] { "*" }, fetchAll: true);

        // Defaults keep RSAT spelling; a raw attribute differing only by case is not
        // re-emitted (PSObject property lookup is case-insensitive, duplicates would be
        // ambiguous). Genuinely different names coexist: Surname AND sn.
        Assert.Equal("John", Value(pso, "GivenName"));
        Assert.Equal("Doe", Value(pso, "Surname"));
        Assert.Equal("Doe", Value(pso, "sn"));
        var names = pso.Properties.Select(p => p.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FetchAll_RangeSuffixedAttributes_EmitUnderTheBaseName()
    {
        // The M1 regression, now at the RSAT layer: a group with >MaxValRange members must
        // still read complete, not silently empty.
        var entry = Entry("CN=X,DC=x", ("memberOf;range=0-1499", new object[] { "CN=G,DC=x" }));
        var pso = ProjectUser(entry, new[] { "*" }, fetchAll: true);

        Assert.NotNull(pso.Properties["memberOf"]);
        Assert.Null(pso.Properties["memberOf;range=0-1499"]);
    }

    // ---- group projection (schema-level; the Get-ADxGroup cmdlet itself is M4) ----

    [Fact]
    public void GroupScopeAndCategory_DecodeFromGroupType()
    {
        var group = Entry("CN=Admins,DC=x",
            ("objectClass", new object[] { "top", "group" }),
            ("groupType", new object[] { "-2147483646" })); // security | global

        var pso = AdRsatProjector.Project(group, AdObjectSchema.Group, null, false);

        Assert.Equal("Global", Value(pso, "GroupScope"));
        Assert.Equal("Security", Value(pso, "GroupCategory"));
        Assert.Equal("group", Value(pso, "ObjectClass"));
    }

    [Fact]
    public void BuiltinGroup_ProjectsAsDomainLocal_NotUnknown()
    {
        // BUILTIN\Administrators: groupType 0x80000005 sets BOTH the builtin-local (0x1) and
        // resource/domain-local (0x4) bits. RSAT's ADGroupScope has no builtin member and
        // reports these as DomainLocal; the filter side agrees, since
        // "GroupScope -eq 'DomainLocal'" tests bit 0x4 and matches this group.
        var builtin = Entry("CN=Administrators,CN=Builtin,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "group" }),
            ("groupType", new object[] { "-2147483643" }));

        var pso = AdRsatProjector.Project(builtin, AdObjectSchema.Group, null, false);

        Assert.Equal("DomainLocal", Value(pso, "GroupScope"));
        Assert.Equal("Security", Value(pso, "GroupCategory"));
    }

    // ---- multi-valued SID attributes ----

    [Fact]
    public void SidHistory_KeepsEveryValue()
    {
        // sIDHistory is Sid-syntax AND multi-valued: a twice-migrated account carries several,
        // and reading only values[0] silently discards the migration trail in the one
        // attribute whose entire purpose is auditing it.
        var user = Entry("CN=Migrated,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("sIDHistory", new object[]
            {
                LdapConvert.SddlToSid("S-1-5-21-1-2-3-1101")!,
                LdapConvert.SddlToSid("S-1-5-21-4-5-6-1102")!,
                LdapConvert.SddlToSid("S-1-5-21-7-8-9-1103")!,
            }));

        var pso = ProjectUser(user, new[] { "SIDHistory" });

        var history = Assert.IsType<ADxSecurityIdentifier?[]>(Value(pso, "SIDHistory"));
        Assert.Equal(3, history.Length);
        Assert.Equal("S-1-5-21-1-2-3-1101", history[0]!.Value);
        Assert.Equal("S-1-5-21-4-5-6-1102", history[1]!.Value);
        Assert.Equal("S-1-5-21-7-8-9-1103", history[2]!.Value);
    }

    [Fact]
    public void SidHistory_WithOneValue_IsStillAnArray()
    {
        // On the AlwaysMultiValued list: scripts index and Count it, so a single value must
        // not collapse to a scalar.
        var user = Entry("CN=Migrated,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("sIDHistory", new object[] { LdapConvert.SddlToSid("S-1-5-21-1-2-3-1101")! }));

        var history = Assert.IsType<ADxSecurityIdentifier?[]>(
            Value(ProjectUser(user, new[] { "SIDHistory" }), "SIDHistory"));

        Assert.Single(history);
    }

    [Fact]
    public void SingleValuedSid_IsStillAScalar()
    {
        // objectSid is Sid-syntax but single-valued: $u.SID.Value must keep working.
        var sid = Assert.IsType<ADxSecurityIdentifier>(Value(ProjectUser(TypicalUser()), "SID"));
        Assert.Equal("S-1-5-21-1-2-3-1013", sid.Value);
    }

    // ---- 0.4: byte-valued attributes the schema previously had no syntax for ----

    [Fact]
    public void FetchAll_ThumbnailPhoto_StaysBytes()
    {
        // The no-escape-hatch corruption path: -Properties * projects every returned
        // attribute, and before 0.4 a photo's bytes fell to the String default and came
        // back as U+FFFD soup. JPEG magic bytes are deliberately not valid UTF-8.
        var photo = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
        var user = Entry("CN=Pic,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("thumbnailPhoto", new object[] { photo }));

        var projected = Assert.IsType<byte[]>(
            Value(ProjectUser(user, new[] { "*" }, fetchAll: true), "thumbnailPhoto"));

        Assert.Equal(photo, projected);
    }

    [Fact]
    public void TokenGroups_ProjectsAsSidArray_EvenWhenSingle()
    {
        // Constructed expanded-membership list: inherently a set, so a lone Domain Users
        // SID must stay an array for scripts that index and Count it.
        var user = Entry("CN=Member,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("tokenGroups", new object[] { LdapConvert.SddlToSid("S-1-5-21-1-2-3-513")! }));

        var groups = Assert.IsType<ADxSecurityIdentifier?[]>(
            Value(ProjectUser(user, new[] { "tokenGroups" }), "tokenGroups"));

        Assert.Single(groups);
        Assert.Equal("S-1-5-21-1-2-3-513", groups[0]!.Value);
    }

    [Fact]
    public void ComputedUserAccountControl_ProjectsAsInt32()
    {
        // Same 2.5.5.9 syntax as userAccountControl; RSAT emits Int32, and before 0.4 the
        // missing table entry made it project as the raw wire string.
        var user = Entry("CN=Locked,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("msDS-User-Account-Control-Computed", new object[] { "8388608" }));

        Assert.Equal(8388608,
            Value(ProjectUser(user, new[] { "msDS-User-Account-Control-Computed" }), "msDS-User-Account-Control-Computed"));
    }

    // ---- 0.4: base-scope-only constructed attribute completion (the tokenGroups fix) ----

    [Fact]
    public void WantedConstructedAttributes_NamesWhatIsRequestedButAbsent()
    {
        var searchEntry = Entry("CN=U,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }));

        // Requested + absent: the follow-up read must fetch it.
        Assert.Equal(new[] { "tokenGroups" },
            ADxObjectCmdletBase.WantedConstructedAttributes(
                searchEntry, new[] { "distinguishedName", "tokenGroups" }));

        // Not requested: never fetched -- -Properties * (fetch list "*") stays untouched,
        // matching RSAT where * omits constructed attributes.
        Assert.Empty(ADxObjectCmdletBase.WantedConstructedAttributes(searchEntry, new[] { "*" }));
        Assert.Empty(ADxObjectCmdletBase.WantedConstructedAttributes(
            searchEntry, new[] { "distinguishedName" }));

        // Already carried (the DN fast path's base read returned it): no second read.
        var baseReadEntry = Entry("CN=U,DC=corp,DC=com",
            ("tokenGroups", new object[] { LdapConvert.SddlToSid("S-1-5-21-1-2-3-513")! }));
        Assert.Empty(ADxObjectCmdletBase.WantedConstructedAttributes(
            baseReadEntry, new[] { "tokenGroups" }));
    }

    [Fact]
    public void MergeConstructedAttributes_OverlaysTheFollowUpValues()
    {
        var searchEntry = Entry("CN=U,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("sAMAccountName", new object[] { "u1" }));
        var followUp = Entry("CN=U,DC=corp,DC=com",
            ("tokenGroups", new object[]
            {
                LdapConvert.SddlToSid("S-1-5-21-1-2-3-513")!,
                LdapConvert.SddlToSid("S-1-5-21-1-2-3-1104")!,
            }));

        var merged = ADxObjectCmdletBase.MergeConstructedAttributes(
            searchEntry, followUp, new[] { "tokenGroups" });

        Assert.Equal(2, merged.Attributes["tokenGroups"].Count);
        Assert.Equal("u1", merged.GetString("sAMAccountName")); // originals preserved

        // The merged entry projects real SIDs -- the confidently-empty array is gone.
        var groups = Assert.IsType<ADxSecurityIdentifier?[]>(
            Value(AdRsatProjector.Project(merged, AdObjectSchema.User, new[] { "tokenGroups" }, false),
                "tokenGroups"));
        Assert.Equal(2, groups.Length);
    }

    [Fact]
    public void MergeConstructedAttributes_FailedOrEmptyFollowUp_LeavesTheEntryUntouched()
    {
        var searchEntry = Entry("CN=U,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }));

        // Absent stays absent: never a fabricated value.
        Assert.Same(searchEntry, ADxObjectCmdletBase.MergeConstructedAttributes(
            searchEntry, null, new[] { "tokenGroups" }));
        Assert.Same(searchEntry, ADxObjectCmdletBase.MergeConstructedAttributes(
            searchEntry, Entry("CN=U,DC=corp,DC=com"), new[] { "tokenGroups" }));
    }

    [Fact]
    public void UserParameters_TextOnWire_StaysString()
    {
        // The transport forces userParameters to byte[] for robustness, but it is a Unicode
        // string attribute: the UTF-8 round-trip is the correct projection, not mojibake.
        var user = Entry("CN=TS,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("userParameters", new object[] { "CtxCfgPresent"u8.ToArray() }));

        Assert.Equal("CtxCfgPresent",
            Value(ProjectUser(user, new[] { "userParameters" }), "userParameters"));
    }

    // ---- 0.2.6: organizational units ----

    private static LdapEntry TypicalOu() => Entry(
        "OU=Sales,DC=corp,DC=com",
        ("objectClass", new object[] { "top", "organizationalUnit" }),
        ("name", new object[] { "Sales" }),
        ("l", new object[] { "Copenhagen" }),
        ("st", new object[] { "Hovedstaden" }),
        ("c", new object[] { "DK" }),
        ("postalCode", new object[] { "1050" }),
        // The OU-specific one: the LDAP attribute is 'street', not 'streetAddress'.
        ("street", new object[] { "Kongens Nytorv 1" }),
        ("managedBy", new object[] { "CN=Boss,OU=Users,DC=corp,DC=com" }),
        ("gPLink", new object[]
        {
            "[LDAP://cn={AAAAAAAA-0000-0000-0000-000000000001},cn=policies,cn=system,DC=corp,DC=com;0]" +
            "[LDAP://cn={BBBBBBBB-0000-0000-0000-000000000002},cn=policies,cn=system,DC=corp,DC=com;2]"
        }),
        ("objectGUID", new object[] { Guid.Parse("01234567-89ab-cdef-0123-456789abcdef").ToByteArray() }));

    private static PSObject ProjectOu(LdapEntry entry, string[]? properties = null, bool fetchAll = false) =>
        AdRsatProjector.Project(entry, AdObjectSchema.OrganizationalUnit, properties, fetchAll);

    [Fact]
    public void OuProjection_StreetAddressComesFromStreet_NotStreetAddress()
    {
        var pso = ProjectOu(TypicalOu());

        Assert.Equal("ADx.OrganizationalUnit", pso.TypeNames[0]);
        Assert.Equal("Kongens Nytorv 1", Value(pso, "StreetAddress"));
        Assert.Equal("Copenhagen", Value(pso, "City"));
        Assert.Equal("DK", Value(pso, "Country"));
        Assert.Equal("Hovedstaden", Value(pso, "State"));
        Assert.Equal("1050", Value(pso, "PostalCode"));
        Assert.Equal("CN=Boss,OU=Users,DC=corp,DC=com", Value(pso, "ManagedBy"));
        Assert.Equal("organizationalUnit", Value(pso, "ObjectClass"));
    }

    [Fact]
    public void OuProjection_LinkedGroupPolicyObjects_IsTheOrderedGpoDnArray()
    {
        var links = Assert.IsType<string[]>(Value(ProjectOu(TypicalOu()), "LinkedGroupPolicyObjects"));

        Assert.Equal(2, links.Length);
        Assert.StartsWith("cn={AAAAAAAA", links[0]);
        Assert.StartsWith("cn={BBBBBBBB", links[1]);
    }

    [Fact]
    public void OuProjection_NoGpLink_LinkedGroupPolicyObjectsIsEmptyArrayNotNull()
    {
        var ou = Entry("OU=Empty,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "organizationalUnit" }),
            ("name", new object[] { "Empty" }));

        var links = Assert.IsType<string[]>(Value(ProjectOu(ou), "LinkedGroupPolicyObjects"));
        Assert.Empty(links);
    }

    [Fact]
    public void StreetOverride_DoesNotLeakToUsers()
    {
        // The override lives on the OU schema only: a USER's StreetAddress must still come
        // from streetAddress. Proves AttributeOverrides is per-schema, not global.
        var user = Entry("CN=U,OU=Users,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "person", "organizationalPerson", "user" }),
            ("streetAddress", new object[] { "User Street 5" }),
            ("street", new object[] { "WRONG - the OU attribute" }));

        Assert.Equal("User Street 5", Value(ProjectUser(user, new[] { "StreetAddress" }), "StreetAddress"));
    }

    [Fact]
    public void OuProjection_PropertiesStar_EmitsBothStreetAndStreetAddress()
    {
        var pso = ProjectOu(TypicalOu(), new[] { "*" }, fetchAll: true);

        // Raw LDAP name is present (fetchAll emits every returned attribute)...
        Assert.Equal("Kongens Nytorv 1", Value(pso, "street"));
        // ...and the RSAT column is surfaced too, via the schema override reverse lookup.
        Assert.Equal("Kongens Nytorv 1", Value(pso, "StreetAddress"));
    }

    [Fact]
    public void OuProjection_PropertiesStreet_EmitsStreetAddressPlusLdapAlias()
    {
        // Asked in LDAP terms: emit the RSAT display name primary AND the ldap name, same
        // value -- the mail/EmailAddress pattern, for the per-schema override.
        var pso = ProjectOu(TypicalOu(), new[] { "street" });

        Assert.Equal("Kongens Nytorv 1", Value(pso, "StreetAddress"));
        Assert.Equal("Kongens Nytorv 1", Value(pso, "street"));
    }

    // ---- 0.2.7: fine-grained password policy (PSO) projection ----

    [Fact]
    public void PsoProjection_MapsRsatNamesToMsDsAttributes_WithCorrectTypes()
    {
        var pso = Entry("CN=StrongPolicy,CN=Password Settings Container,CN=System,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "msDS-PasswordSettings" }),
            ("name", new object[] { "StrongPolicy" }),
            ("msDS-PasswordSettingsPrecedence", new object[] { "10" }),
            ("msDS-MinimumPasswordLength", new object[] { "14" }),
            // 30 days, stored negative (interval).
            ("msDS-MaximumPasswordAge", new object[] { (-TimeSpan.FromDays(30).Ticks).ToString() }),
            ("msDS-PasswordComplexityEnabled", new object[] { "TRUE" }),
            ("msDS-PasswordReversibleEncryptionEnabled", new object[] { "FALSE" }),
            ("msDS-PSOAppliesTo", new object[] { "CN=Admins,DC=corp,DC=com", "CN=Ops,DC=corp,DC=com" }));

        var result = AdRsatProjector.Project(pso, AdObjectSchema.FineGrainedPasswordPolicy, null, false);

        Assert.Equal("ADx.FineGrainedPasswordPolicy", result.TypeNames[0]);
        Assert.Equal("StrongPolicy", Value(result, "Name"));
        // Int32, not Int64: AD's Integer syntax is 32-bit and RSAT emits int.
        Assert.Equal(10, Value(result, "Precedence"));
        Assert.Equal(14, Value(result, "MinPasswordLength"));
        // Interval -> positive TimeSpan, exactly like the domain-head policy cmdlet.
        Assert.Equal(TimeSpan.FromDays(30), Value(result, "MaxPasswordAge"));
        Assert.Equal(true, Value(result, "ComplexityEnabled"));
        Assert.Equal(false, Value(result, "ReversibleEncryptionEnabled"));
    }

    [Theory]
    // A preset must accept its OWN default properties via -Properties. The PSO override-only
    // names live only in the schema's AttributeOverrides, absent from the global tables, so
    // validation must consult the overrides -- else the cmdlet rejects its own columns.
    [InlineData("AppliesTo")]
    [InlineData("Precedence")]
    [InlineData("ComplexityEnabled")]
    [InlineData("ReversibleEncryptionEnabled")]
    [InlineData("MaxPasswordAge")]
    public void PsoOverrideNames_AreValidProperties_WithoutTheEscapeHatch(string name)
    {
        // Must NOT throw, even with allowUnknown = false.
        AdRsatProjector.ValidateRequestedProperties(
            new[] { name }, allowUnknown: false,
            AdObjectSchema.FineGrainedPasswordPolicy.AttributeOverrides);
    }

    [Fact]
    public void OverrideValidation_DoesNotLeakToSchemasWithoutTheOverride()
    {
        // A PSO-only name is still rejected for a schema whose overrides don't include it.
        Assert.Throws<AdFilterTranslationException>(() =>
            AdRsatProjector.ValidateRequestedProperties(
                new[] { "AppliesTo" }, allowUnknown: false, AdObjectSchema.User.AttributeOverrides));
    }

    [Fact]
    public void PsoProjection_AppliesTo_IsAlwaysADnArray()
    {
        var multi = Entry("CN=P,CN=Password Settings Container,CN=System,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "msDS-PasswordSettings" }),
            ("msDS-PSOAppliesTo", new object[] { "CN=A,DC=corp,DC=com", "CN=B,DC=corp,DC=com" }));
        var single = Entry("CN=Q,CN=Password Settings Container,CN=System,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "msDS-PasswordSettings" }),
            ("msDS-PSOAppliesTo", new object[] { "CN=A,DC=corp,DC=com" }));
        var none = Entry("CN=R,CN=Password Settings Container,CN=System,DC=corp,DC=com",
            ("objectClass", new object[] { "top", "msDS-PasswordSettings" }));

        var m = Assert.IsType<string[]>(Value(
            AdRsatProjector.Project(multi, AdObjectSchema.FineGrainedPasswordPolicy, null, false), "AppliesTo"));
        var s = Assert.IsType<string[]>(Value(
            AdRsatProjector.Project(single, AdObjectSchema.FineGrainedPasswordPolicy, null, false), "AppliesTo"));
        var n = Assert.IsType<string[]>(Value(
            AdRsatProjector.Project(none, AdObjectSchema.FineGrainedPasswordPolicy, null, false), "AppliesTo"));

        Assert.Equal(2, m.Length);
        Assert.Single(s);      // a single value must NOT collapse to a scalar
        Assert.Empty(n);       // absent -> empty array, not null
    }
}
