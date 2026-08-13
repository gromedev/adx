using System.Globalization;

namespace ADx.Engine.Filter;

/// <summary>
/// Filter-time translation for AD's synthetic properties -- ones with no LDAP attribute of
/// their own, derived instead from bits packed into <c>userAccountControl</c>/<c>groupType</c>
/// or a threshold on <c>lockoutTime</c>. RSAT exposes these as ordinary boolean or string
/// properties (<c>Enabled -eq $true</c>, <c>GroupScope -eq 'Global'</c>); getting the bit or
/// threshold wrong here is the same "0 rows, success" failure class as any other typed-value
/// marshalling bug, so each rule is implemented once rather than reconstructed per cmdlet.
/// </summary>
public static class AdSyntheticProperties
{
    /// <summary>
    /// Boolean synthetic properties backed by a <c>userAccountControl</c> bit, keyed by RSAT
    /// name. <c>TrueMeansBitSet</c> is false only for <c>Enabled</c>, whose sense is inverted
    /// from the underlying <c>ADS_UF_ACCOUNTDISABLE</c> bit.
    /// </summary>
    private static readonly Dictionary<string, (uint Mask, bool TrueMeansBitSet)> UacBooleans =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Enabled"] = (0x2, false),
            ["PasswordNeverExpires"] = (0x10000, true),
            ["PasswordNotRequired"] = (0x20, true),
            ["SmartcardLogonRequired"] = (0x40000, true),
            ["TrustedForDelegation"] = (0x80000, true),
            ["TrustedToAuthForDelegation"] = (0x1000000, true),
            ["AccountNotDelegated"] = (0x100000, true),
            ["DoesNotRequirePreAuth"] = (0x400000, true),
            ["AllowReversiblePasswordEncryption"] = (0x80, true),
            ["UseDESKeyOnly"] = (0x200000, true),
            ["HomedirRequired"] = (0x8, true),
            ["MNSLogonAccount"] = (0x20000, true),
        };

    private static readonly HashSet<string> StringEnumNames =
        new(StringComparer.OrdinalIgnoreCase) { "GroupScope", "GroupCategory" };

    private static readonly Dictionary<string, uint> GroupScopeBits =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BuiltinLocal"] = 0x1,
            ["Global"] = 0x2,
            ["DomainLocal"] = 0x4,
            ["Universal"] = 0x8,
        };

    /// <summary>
    /// Recognised but deliberately not translatable: each needs more than a filter can express
    /// (a security-descriptor ACE walk, client-side DNS resolution, a bind-response-only
    /// computed attribute, or a domain-SID-dependent DN lookup). Rejecting these explicitly
    /// beats emitting a filter against a name that doesn't exist on the wire, which is the
    /// "silently returns the wrong set" failure this whole design guards against.
    /// </summary>
    public static readonly IReadOnlySet<string> UnsupportedForFiltering =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PasswordExpired", "KerberosEncryptionType", "CompoundIdentitySupported",
            "PrimaryGroup", "IPv4Address", "IPv6Address", "ProtectedFromAccidentalDeletion",
            "PrincipalsAllowedToDelegateToAccount", "PrincipalsAllowedToRetrieveManagedPassword",
        };

    /// <summary>
    /// Constructed attributes the DC computes per read and refuses to evaluate in a filter.
    /// A comparison against them is structurally valid LDAP that matches nothing -- success
    /// code, zero rows, silently -- which is exactly the failure class the loud refusals
    /// exist to prevent. Keyed by LDAP name; the value is the actionable redirect for the
    /// error message. Distinct from <see cref="UnsupportedForFiltering"/> (RSAT display-name
    /// synthetics): these are real wire attributes that project fine but cannot filter.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> UnfilterableConstructedAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tokenGroups"] =
                "filter on memberOf (with -RecursiveMatch for nesting) and read tokenGroups from the resolved objects",
            ["msDS-User-Account-Control-Computed"] =
                "use Search-ADxAccount's switches (-LockedOut, -PasswordExpired), which evaluate it client-side",
            ["primaryGroupToken"] =
                "filter member objects on primaryGroupID instead",
        };

    /// <summary>Takes $true/$false: the UAC-bit properties plus LockedOut.</summary>
    public static bool IsBooleanSynthetic(string name) =>
        UacBooleans.ContainsKey(name) || name.Equals("LockedOut", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The UAC bit behind a boolean synthetic, for the OUTPUT projector -- decoding a fetched
    /// <c>userAccountControl</c> into the same named booleans the filter side matches on.
    /// One table serving both directions is the point: a property that filters one way and
    /// projects another is the bug class this schema exists to kill.
    /// </summary>
    public static bool TryGetUacBit(string name, out uint mask, out bool trueMeansBitSet)
    {
        if (UacBooleans.TryGetValue(name, out var def))
        {
            (mask, trueMeansBitSet) = def;
            return true;
        }
        mask = 0;
        trueMeansBitSet = false;
        return false;
    }

    /// <summary>The RSAT names of all UAC-backed booleans (for property validation).</summary>
    public static IEnumerable<string> UacBooleanNames => UacBooleans.Keys;

    /// <summary>Takes an enum-name string: GroupScope and GroupCategory.</summary>
    public static bool IsStringSynthetic(string name) => StringEnumNames.Contains(name);

    public static bool IsKnownSyntheticProperty(string name) =>
        IsBooleanSynthetic(name) || IsStringSynthetic(name);

    /// <summary>Build the node for "PropertyName -eq/-ne $true/$false" on a boolean synthetic property.</summary>
    public static bool TryEmitBooleanEquality(string propertyName, bool value, out AdFilterNode node)
    {
        if (UacBooleans.TryGetValue(propertyName, out var def))
        {
            var bit = UacBit(def.Mask);
            var bitMeansTrue = def.TrueMeansBitSet == value;
            node = bitMeansTrue ? bit : new AdFilterNot(bit);
            return true;
        }

        if (propertyName.Equals("LockedOut", StringComparison.OrdinalIgnoreCase))
        {
            // lockoutTime is 0 when not locked out and a FILETIME timestamp otherwise, so
            // "locked out" is simply "the attribute holds a value of at least 1".
            var threshold = new AdFilterGreaterOrEqual("lockoutTime", LdapAssertionValue.Verbatim("1"));
            node = value ? threshold : new AdFilterNot(threshold);
            return true;
        }

        node = null!;
        return false;
    }

    /// <summary>Build the node for "GroupScope -eq 'Global'" / "GroupCategory -eq 'Security'".</summary>
    public static bool TryEmitStringEquality(string propertyName, string value, out AdFilterNode node)
    {
        if (propertyName.Equals("GroupCategory", StringComparison.OrdinalIgnoreCase))
        {
            // groupType's sign bit (0x80000000): AD's own documented convention for querying
            // it in a filter is the decimal form of that bit, 2147483648.
            var securityBit = GroupTypeBit(0x80000000);
            if (value.Equals("Security", StringComparison.OrdinalIgnoreCase))
            {
                node = securityBit;
                return true;
            }
            if (value.Equals("Distribution", StringComparison.OrdinalIgnoreCase))
            {
                node = new AdFilterNot(securityBit);
                return true;
            }
            throw new AdFilterTranslationException(
                $"GroupCategory must be 'Security' or 'Distribution', not '{value}'.");
        }

        if (propertyName.Equals("GroupScope", StringComparison.OrdinalIgnoreCase))
        {
            if (!GroupScopeBits.TryGetValue(value, out var bit))
                throw new AdFilterTranslationException(
                    $"GroupScope must be one of BuiltinLocal, Global, DomainLocal, Universal, not '{value}'.");

            node = GroupTypeBit(bit);
            return true;
        }

        node = null!;
        return false;
    }

    private static AdFilterNode UacBit(uint mask) =>
        new AdFilterBitAnd("userAccountControl", LdapAssertionValue.Verbatim(mask.ToString(CultureInfo.InvariantCulture)));

    private static AdFilterNode GroupTypeBit(uint mask) =>
        new AdFilterBitAnd("groupType", LdapAssertionValue.Verbatim(mask.ToString(CultureInfo.InvariantCulture)));
}
