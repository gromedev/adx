using System.Management.Automation;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Base;

/// <summary>
/// The RSAT-fidelity output projector: <see cref="LdapEntry"/> in, PSObject with RSAT
/// PascalCase property names out. This is the second of the two projectors sharing
/// <see cref="AdAttributeSchema"/> -- <c>Search-ADxObject</c> keeps raw LDAP names and UTC
/// <c>DateTimeOffset</c>s; this one exists so scripts written against <c>Get-ADUser</c> keep
/// working unchanged. The three fidelity decisions from the plan:
/// <list type="bullet">
/// <item><c>ObjectClass</c> is a single string (the most specific class, i.e. the last array
/// element) -- <c>$u.ObjectClass -eq 'user'</c> breaks otherwise.</item>
/// <item>Dates are local <see cref="DateTime"/>, matching RSAT --
/// <c>$u.whenCreated -lt (Get-Date).AddDays(-90)</c> must behave identically.</item>
/// <item><c>SID</c> is an <see cref="ADxSecurityIdentifier"/>, because scripts read
/// <c>$u.SID.Value</c> and a bare string breaks them; the real
/// <c>SecurityIdentifier</c> cannot be constructed off Windows.</item>
/// </list>
/// </summary>
internal static class AdRsatProjector
{
    /// <summary>
    /// RSAT property names that exist in RSAT's output but that ADx cannot yet produce.
    /// Declared unsupported with an explicit error rather than silently emitting null --
    /// each needs machinery beyond an attribute read (a domain-SID join for PrimaryGroup,
    /// client-side DNS for IPv4/IPv6Address, an ACE walk for ProtectedFromAccidentalDeletion,
    /// a security-descriptor parse for PrincipalsAllowedToDelegateToAccount).
    /// </summary>
    private static readonly HashSet<string> UnsupportedOutputProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "PrimaryGroup", "IPv4Address", "IPv6Address", "ProtectedFromAccidentalDeletion",
        "PrincipalsAllowedToDelegateToAccount", "KerberosEncryptionType", "CompoundIdentitySupported",
    };

    /// <summary>
    /// Attributes always emitted as arrays even with a single value, because scripts index
    /// and Count them. Everything else is scalar-when-single.
    /// </summary>
    private static readonly HashSet<string> AlwaysMultiValued = new(StringComparer.OrdinalIgnoreCase)
    {
        "memberOf", "member", "servicePrincipalName", "proxyAddresses", "sIDHistory",
        "userCertificate", "description",
    };

    /// <summary>
    /// Output synthetics with no single backing attribute of their own, or with value shapes
    /// the generic syntax conversion cannot produce. Names here are accepted in -Properties.
    /// </summary>
    private static readonly HashSet<string> OutputSynthetics = new(StringComparer.OrdinalIgnoreCase)
    {
        "LockedOut", "AccountLockoutTime", "PasswordExpired",
        "GroupScope", "GroupCategory",
        // gPLink is a single packed string; RSAT surfaces it as an ordered array of GPO DNs.
        "LinkedGroupPolicyObjects",
    };

    /// <summary>What an output synthetic needs fetched.</summary>
    private static readonly Dictionary<string, string> SyntheticFetchAttribute = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LockedOut"] = "lockoutTime",
        ["AccountLockoutTime"] = "lockoutTime",
        // Constructed attribute: the UAC PasswordExpired bit (0x800000) is only meaningful in
        // msDS-User-Account-Control-Computed, not in the stored userAccountControl.
        ["PasswordExpired"] = "msDS-User-Account-Control-Computed",
        ["GroupScope"] = "groupType",
        ["GroupCategory"] = "groupType",
        ["LinkedGroupPolicyObjects"] = "gPLink",
    };

    /// <summary>
    /// Resolve a requested property to its LDAP attribute, consulting the schema's per-type
    /// overrides (an OU's <c>StreetAddress</c> is the <c>street</c> attribute) BEFORE the
    /// global alias/known-name ladder. This is the one place the OU/user divergence lives.
    /// </summary>
    private static bool TryResolveAttribute(
        IReadOnlyDictionary<string, string>? overrides, string name, out string ldapName)
    {
        if (overrides is not null && overrides.TryGetValue(name, out var overridden))
        {
            ldapName = overridden;
            return true;
        }
        return AdAttributeSchema.TryResolveAttributeName(name, out ldapName);
    }

    /// <summary>
    /// The reverse of a schema override: given an LDAP attribute this schema remaps
    /// (<c>street</c>), the display name it should surface as (<c>StreetAddress</c>). Lets a
    /// caller who asked in LDAP terms, or <c>-Properties *</c>, still get the RSAT column.
    /// </summary>
    private static bool TryGetSchemaDisplayName(
        IReadOnlyDictionary<string, string>? overrides, string ldapName, out string displayName)
    {
        if (overrides is not null)
        {
            foreach (var (rsat, ldap) in overrides)
            {
                if (ldap.Equals(ldapName, StringComparison.OrdinalIgnoreCase))
                {
                    displayName = rsat;
                    return true;
                }
            }
        }
        displayName = string.Empty;
        return false;
    }

    /// <summary>
    /// Validate the requested extra properties and build the LDAP fetch list for
    /// defaults-plus-extras. "*" anywhere in <paramref name="extraProperties"/> switches to
    /// fetch-everything: the literal <c>"*"</c> is sent (RSAT does the same), which notably
    /// does NOT return constructed attributes -- RSAT's <c>-Properties *</c> omits them too,
    /// so that parity is free.
    /// </summary>
    public static IReadOnlyList<string> BuildFetchList(
        AdObjectSchema schema, string[]? extraProperties, bool allowUnknown, out bool fetchAll)
    {
        fetchAll = extraProperties is not null &&
                   extraProperties.Any(p => p is "*");

        var attributes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAttribute(string ldapName)
        {
            if (seen.Add(ldapName)) attributes.Add(ldapName);
        }

        void AddForProperty(string propertyName)
        {
            if (SyntheticFetchAttribute.TryGetValue(propertyName, out var syntheticSource))
            {
                AddAttribute(syntheticSource);
                return;
            }
            if (AdSyntheticProperties.TryGetUacBit(propertyName, out _, out _))
            {
                AddAttribute("userAccountControl");
                return;
            }
            if (TryResolveAttribute(schema.AttributeOverrides, propertyName, out var ldapName))
            {
                AddAttribute(ldapName);
                return;
            }
            // Callers validated already; unknown-but-allowed pass through verbatim.
            AddAttribute(propertyName);
        }

        if (fetchAll)
        {
            attributes.Add("*");
            seen.Add("*");
            // Named extras ride along explicitly: harmless for plain attributes ("*" covers
            // them anyway), REQUIRED for constructed ones (msDS-User-Account-Control-Computed
            // and friends), which "*" never returns. This is how RSAT's "-Properties *,x"
            // behaves too.
            foreach (var extra in extraProperties!)
            {
                if (extra is "*") continue;
                AddForProperty(extra);
            }
            return attributes;
        }

        foreach (var name in schema.DefaultProperties) AddForProperty(name);
        if (extraProperties is not null)
            foreach (var name in extraProperties) AddForProperty(name);

        return attributes;
    }

    /// <summary>
    /// Validate -Properties names up front. A misspelled property name must be a terminating
    /// error before any query runs: AD itself ignores unknown attributes in the requested
    /// list and the emitted column would just be null -- RSAT errors here, and so do we.
    /// </summary>
    public static void ValidateRequestedProperties(string[]? extraProperties, bool allowUnknown)
    {
        if (extraProperties is null) return;

        foreach (var name in extraProperties)
        {
            if (name is "*") continue;

            if (string.IsNullOrWhiteSpace(name))
                throw new AdFilterTranslationException("-Properties contains an empty name.");

            if (UnsupportedOutputProperties.Contains(name))
                throw new AdFilterTranslationException(
                    $"'-Properties {name}' is not supported by ADx: it needs data outside a plain attribute " +
                    "read (a security descriptor, DNS, or a cross-object join). This is a declared gap, " +
                    "not a schema miss.");

            if (OutputSynthetics.Contains(name)) continue;
            if (AdSyntheticProperties.TryGetUacBit(name, out _, out _)) continue;
            if (AdAttributeSchema.TryResolveAttributeName(name, out _)) continue;

            if (!allowUnknown)
                throw new AdFilterTranslationException(
                    $"'-Properties {name}' is not a known attribute or RSAT property name. AD silently ignores " +
                    "unknown names in a request list, so this would just emit a null column. Fix the name, or " +
                    "pass -AllowUnknownProperty if the attribute genuinely exists in your schema.");
        }
    }

    /// <summary>Project one entry under RSAT naming for the given schema and extras.</summary>
    public static PSObject Project(
        LdapEntry entry, AdObjectSchema schema, string[]? extraProperties, bool fetchAll)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, $"ADx.{char.ToUpperInvariant(schema.TypeLabel[0])}{schema.TypeLabel.Substring(1)}");
        pso.TypeNames.Insert(1, ADxCmdletBase.EntryTypeName);

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Emit(string name, object? value)
        {
            if (emitted.Add(name)) pso.Properties.Add(new PSNoteProperty(name, value));
        }

        var overrides = schema.AttributeOverrides;

        // Defaults first, in table order, always present (null when absent) -- RSAT keeps
        // requested properties on the object even when the directory returned nothing.
        foreach (var name in schema.DefaultProperties)
            Emit(name, ProjectProperty(entry, name, overrides));

        if (extraProperties is not null)
        {
            foreach (var requested in extraProperties)
            {
                if (requested is "*") continue;

                Emit(CanonicalOutputName(requested, overrides, out var alsoEmitLdapAlias, out var ldapAliasName),
                    ProjectProperty(entry, requested, overrides));

                // The caller asked in LDAP terms for an attribute whose curated RSAT name
                // differs (-Properties mail): emit BOTH names, same value. Kills a whole
                // class of drop-in failures cheaply.
                if (alsoEmitLdapAlias)
                    Emit(ldapAliasName, ProjectProperty(entry, requested, overrides));
            }
        }

        if (fetchAll)
        {
            // Everything the server returned, under canonical LDAP names, converted by
            // syntax; plus the curated RSAT name where one exists. Case-insensitive dedup
            // means attributes already consumed by a default keep the RSAT spelling
            // (GivenName wins over givenName), while genuinely different names coexist
            // (Surname and sn).
            foreach (var attributeName in entry.Attributes.Keys)
            {
                var baseName = LdapEntry.TryParseRangeOption(attributeName, out var parsed, out _, out _, out _)
                    ? parsed!
                    : attributeName;
                var canonical = AdAttributeSchema.TryResolveAttributeName(baseName, out var resolved)
                    ? resolved
                    : baseName;

                Emit(canonical, ConvertBySyntax(entry, attributeName, canonical));

                if (AdAttributeSchema.TryGetRsatNameForLdapAttribute(canonical, out var rsatName))
                    Emit(rsatName, ProjectProperty(entry, rsatName, overrides));

                // A per-schema override (street -> StreetAddress) has no global reverse alias,
                // so surface the RSAT column here too rather than leaving an OU's -Properties *
                // with only the raw 'street'.
                if (TryGetSchemaDisplayName(overrides, canonical, out var schemaDisplay))
                    Emit(schemaDisplay, ProjectProperty(entry, schemaDisplay, overrides));
            }
        }

        return pso;
    }

    /// <summary>
    /// The output property name for a requested name: curated RSAT names keep their table
    /// casing; LDAP names keep canonical LDAP casing. When the request was the LDAP spelling
    /// of an attribute with a curated RSAT name, the RSAT name is primary and
    /// <paramref name="alsoEmitLdapAlias"/> asks the caller to add the LDAP name too.
    /// </summary>
    private static string CanonicalOutputName(
        string requested, IReadOnlyDictionary<string, string>? overrides,
        out bool alsoEmitLdapAlias, out string ldapAliasName)
    {
        alsoEmitLdapAlias = false;
        ldapAliasName = string.Empty;

        if (OutputSynthetics.Contains(requested) || AdSyntheticProperties.TryGetUacBit(requested, out _, out _))
            return requested;

        // Per-schema override, both directions, before the global ladder. Asked by display
        // name (StreetAddress) -> keep it. Asked by the overridden LDAP name (street) -> the
        // display name is primary and the LDAP name rides along, mirroring the mail/EmailAddress
        // behaviour below.
        if (overrides is not null)
        {
            if (overrides.ContainsKey(requested)) return requested;
            if (TryGetSchemaDisplayName(overrides, requested, out var display))
            {
                alsoEmitLdapAlias = true;
                ldapAliasName = requested;
                return display;
            }
        }

        if (AdAttributeSchema.TryResolveAttributeName(requested, out var ldapName))
        {
            if (AdAttributeSchema.TryGetRsatNameForLdapAttribute(ldapName, out var rsatName))
            {
                // Was the request already the RSAT spelling? Then just that name; otherwise
                // the caller typed the LDAP name and gets both.
                if (!rsatName.Equals(requested, StringComparison.OrdinalIgnoreCase))
                {
                    alsoEmitLdapAlias = true;
                    ldapAliasName = ldapName;
                }
                return rsatName;
            }
            return ldapName;
        }

        return requested;
    }

    /// <summary>Produce the value for one RSAT/LDAP property name from the entry.</summary>
    private static object? ProjectProperty(
        LdapEntry entry, string name, IReadOnlyDictionary<string, string>? overrides = null)
    {
        // Structural names first.
        if (name.Equals("DistinguishedName", StringComparison.OrdinalIgnoreCase))
            return entry.DistinguishedName;

        if (name.Equals("LinkedGroupPolicyObjects", StringComparison.OrdinalIgnoreCase))
            // Always an array (empty when there are no links) so .Count/indexing work.
            return AdTopology.ParseGpLink(entry.GetString("gPLink")).ToArray();

        if (name.Equals("ObjectClass", StringComparison.OrdinalIgnoreCase))
        {
            var classes = entry.GetStrings("objectClass");
            return classes.Count > 0 ? classes[^1] : null;
        }

        // UAC-backed booleans (Enabled and friends). Absent userAccountControl -> null,
        // matching "the attribute was not fetched / not present", not a guessed false.
        if (AdSyntheticProperties.TryGetUacBit(name, out var mask, out var trueMeansSet))
        {
            var uac = entry.GetInt64("userAccountControl");
            if (uac is null) return null;
            var bitSet = ((uint)uac.Value & mask) != 0;
            return trueMeansSet ? bitSet : !bitSet;
        }

        if (name.Equals("LockedOut", StringComparison.OrdinalIgnoreCase))
        {
            var lockout = entry.GetInt64("lockoutTime");
            return lockout is > 0;
        }

        if (name.Equals("AccountLockoutTime", StringComparison.OrdinalIgnoreCase))
            return ToLocalDateTime(LdapConvert.FileTime(entry.GetString("lockoutTime")));

        if (name.Equals("PasswordExpired", StringComparison.OrdinalIgnoreCase))
        {
            var computedUac = entry.GetInt64("msDS-User-Account-Control-Computed");
            if (computedUac is null) return null;
            return ((uint)computedUac.Value & 0x800000) != 0;
        }

        if (name.Equals("GroupScope", StringComparison.OrdinalIgnoreCase))
        {
            var groupType = entry.GetInt32("groupType");
            if (groupType is null) return null;

            var scope = LdapConvert.GroupType(groupType.Value).Scope;
            // RSAT's ADGroupScope enum has three members: DomainLocal, Global, Universal.
            // Builtin groups (groupType 0x80000005, BUILTIN_LOCAL_GROUP | RESOURCE_GROUP)
            // report as DomainLocal there, and the filter side agrees -- the DomainLocal
            // bit test (0x4) matches them. Emitting "BuiltinLocal" here would make
            // Get-ADxGroup -Filter "GroupScope -eq 'DomainLocal'" return objects whose
            // projected GroupScope contradicts the filter that selected them.
            return scope == GroupScopeKind.BuiltinLocal
                ? nameof(GroupScopeKind.DomainLocal)
                : scope.ToString();
        }

        if (name.Equals("GroupCategory", StringComparison.OrdinalIgnoreCase))
        {
            var groupType = entry.GetInt32("groupType");
            if (groupType is null) return null;
            return LdapConvert.GroupType(groupType.Value).IsSecurity ? "Security" : "Distribution";
        }

        // Everything else: resolve to the LDAP attribute (schema override first) and convert
        // by its syntax.
        var attribute = TryResolveAttribute(overrides, name, out var resolved) ? resolved : name;
        return ConvertBySyntax(entry, attribute, attribute);
    }

    /// <summary>
    /// Syntax-driven wire-to-.NET conversion, RSAT flavour: local DateTimes,
    /// ADxSecurityIdentifier SIDs. <paramref name="lookupName"/> is the key into the entry
    /// (may carry a ;range= suffix); <paramref name="canonicalName"/> drives the syntax.
    /// </summary>
    private static object? ConvertBySyntax(LdapEntry entry, string lookupName, string canonicalName)
    {
        if (!entry.Attributes.ContainsKey(lookupName))
        {
            // Range-suffixed reads: the caller asked for "member" but the server returned
            // "member;range=0-1499".
            if (!entry.TryGetRanged(lookupName, out var rangedValues, out _, out _, out _))
                return AlwaysMultiValued.Contains(canonicalName) ? Array.Empty<string>() : null;
            return rangedValues.ToArray();
        }

        switch (AdAttributeSchema.SyntaxOf(canonicalName))
        {
            case AdAttributeSyntax.Sid:
            {
                // Every value, not just the first: sIDHistory is Sid-syntax AND multi-valued,
                // and a twice-migrated account legitimately carries several. GetBytes returns
                // only values[0], so reading it directly dropped the rest silently -- in the
                // one attribute whose whole purpose is auditing migrated access.
                var sids = entry.Attributes[lookupName]
                    .Select(v => ADxSecurityIdentifier.FromBinary(v as byte[]))
                    .Where(s => s is not null)
                    .ToArray();

                if (AlwaysMultiValued.Contains(canonicalName)) return sids;
                return sids.Length switch
                {
                    0 => null,
                    1 => sids[0],
                    _ => sids
                };
            }

            case AdAttributeSyntax.Guid:
                return LdapConvert.ObjectGuid(entry.GetBytes(lookupName));

            case AdAttributeSyntax.GeneralizedTime:
                return ToLocalDateTime(LdapConvert.GeneralizedTime(entry.GetString(lookupName)));

            case AdAttributeSyntax.FileTime:
                return ToLocalDateTime(LdapConvert.FileTime(entry.GetString(lookupName)));

            case AdAttributeSyntax.Interval:
                // Durations, not timestamps: no local-time conversion applies. RSAT emits
                // positive TimeSpans and so does LdapConvert.Interval.
                return LdapConvert.Interval(entry.GetString(lookupName));

            case AdAttributeSyntax.Integer:
                return entry.GetInt64(lookupName);

            case AdAttributeSyntax.Boolean:
            {
                var text = entry.GetString(lookupName);
                if (text is null) return null;
                return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            }

            case AdAttributeSyntax.Binary:
            {
                var values = entry.Attributes[lookupName];
                var bytes = values.OfType<byte[]>().ToArray();
                return bytes.Length switch
                {
                    0 => null,
                    1 when !AlwaysMultiValued.Contains(canonicalName) => bytes[0],
                    _ => bytes
                };
            }

            default: // String, Dn
            {
                var strings = entry.GetStrings(lookupName);
                if (AlwaysMultiValued.Contains(canonicalName)) return strings.ToArray();
                return strings.Count switch
                {
                    0 => null,
                    1 => strings[0],
                    _ => strings.ToArray()
                };
            }
        }
    }

    /// <summary>
    /// RSAT emits LOCAL DateTimes; the engine's converters return UTC DateTimeOffsets. The
    /// conversion to local happens exactly here, at the RSAT projection boundary, so
    /// Search-ADxObject stays UTC.
    /// </summary>
    private static DateTime? ToLocalDateTime(DateTimeOffset? value) => value?.LocalDateTime;
}
