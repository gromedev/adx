namespace ADx.Engine.Ldap;

/// <summary>
/// Per-object-type table driving the RSAT-compatible preset cmdlets: the base object-class
/// filter, the default property set, and how <c>-Identity</c> may be spelled. Each preset
/// cmdlet is one of these plus a <c>[Cmdlet]</c> attribute -- the behaviour all lives in the
/// shared base class and projector, keyed off this record.
/// </summary>
/// <param name="TypeLabel">Lowercase type name, used in messages and PSTypeNames ("user").</param>
/// <param name="BaseFilter">
/// LDAP filter ANDed with every search this preset runs; null for the untyped Get-ADxObject.
/// For users this must be <c>(&amp;(objectCategory=person)(objectClass=user))</c> --
/// <c>objectClass=user</c> alone also matches computers, whose class derives from user.
/// </param>
/// <param name="RequiredClass">
/// The class an object must carry ANYWHERE in its multi-valued <c>objectClass</c> to be this
/// type, used to verify an <c>-Identity</c> resolved by DN: a base-scope read applies no
/// filter, so without this check a group DN passed to Get-ADxUser would happily return the
/// group. Null skips the check (Get-ADxObject).
/// <para>
/// Membership in the chain, not "is the last element": AD's class hierarchy is real, and an
/// <c>inetOrgPerson</c> account's chain ends in <c>inetOrgPerson</c> while still being every
/// bit a user. Requiring the last element made the DN fast path reject objects that the
/// -Filter path (whose wire filter <c>objectClass=user</c> matches derived classes) happily
/// returned -- the same cmdlet answering two different ways depending on how it was asked.
/// <see cref="DisqualifyingClasses"/> carries the other half of the rule.
/// </para>
/// </param>
/// <param name="DisqualifyingClasses">
/// Classes whose presence means the object is a DIFFERENT type, even though
/// <see cref="RequiredClass"/> matched. This mirrors the base filter exactly: a computer's
/// chain contains <c>user</c>, which is why <c>Get-ADxUser</c>'s wire filter pairs
/// <c>objectClass=user</c> with <c>objectCategory=person</c>. Without this, widening the
/// check to "anywhere in the chain" would make Get-ADxUser return computers.
/// </param>
/// <param name="DefaultProperties">
/// RSAT display names emitted when <c>-Properties</c> is not given; additive with it when it
/// is. From the plan's table; live-DC ground-truthing is deferred (no DC reachable).
/// </param>
/// <param name="IdentityIncludesSamAccountName">
/// Whether a plain string identity falls through to sAMAccountName (true for user/group/
/// computer; false for Get-ADxObject, which accepts DN or GUID only, matching RSAT).
/// </param>
/// <param name="IdentitySamTriesDollarSuffix">
/// Computer accounts' sAMAccountName ends in '$', which nobody types; when the plain form
/// misses, retry with the suffix.
/// </param>
/// <param name="AttributeOverrides">
/// Per-type RSAT-name -&gt; LDAP-attribute mappings consulted BEFORE the global alias table,
/// for the display names whose backing attribute genuinely differs by object class: an OU's
/// <c>StreetAddress</c> is the LDAP <c>street</c> attribute where a user's is
/// <c>streetAddress</c>. Null (every existing preset) means the global ladder alone applies
/// -- resolving through the global table for such a name would fetch the wrong attribute
/// and silently emit null, the exact failure class this module exists to prevent.
/// </param>
/// <param name="IdentityByName">
/// Whether a bare-string <c>-Identity</c> that is not a DN/GUID/SID resolves by the object's
/// <c>name</c> (cn) attribute. For types with no sAMAccountName -- fine-grained password
/// policies -- whose RSAT cmdlet takes the object's name. Mutually exclusive with
/// <see cref="IdentityIncludesSamAccountName"/> in practice.
/// </param>
/// <param name="DefaultContainerRelativeDn">
/// When set and no <c>-SearchBase</c> is given, searches default to this container relative to
/// the domain's defaultNamingContext (e.g. <c>CN=Password Settings Container,CN=System</c>),
/// not the domain root -- matching RSAT's default base for a type confined to one container.
/// Null (every other preset) keeps the domain-root default.
/// </param>
public sealed record AdObjectSchema(
    string TypeLabel,
    string? BaseFilter,
    string? RequiredClass,
    IReadOnlyList<string> DefaultProperties,
    bool IdentityIncludesSamAccountName,
    bool IdentitySamTriesDollarSuffix,
    IReadOnlyList<string>? DisqualifyingClasses = null,
    IReadOnlyDictionary<string, string>? AttributeOverrides = null,
    bool IdentityByName = false,
    string? DefaultContainerRelativeDn = null)
{
    public static readonly AdObjectSchema User = new(
        "user",
        "(&(objectCategory=person)(objectClass=user))",
        "user",
        new[]
        {
            "DistinguishedName", "Enabled", "GivenName", "Name", "ObjectClass", "ObjectGUID",
            "SamAccountName", "SID", "Surname", "UserPrincipalName"
        },
        IdentityIncludesSamAccountName: true,
        IdentitySamTriesDollarSuffix: false,
        // The computer class derives from user: without this a computer's DN would resolve
        // through Get-ADxUser, exactly what objectCategory=person prevents on the wire.
        DisqualifyingClasses: new[] { "computer" });

    public static readonly AdObjectSchema Group = new(
        "group",
        "(objectCategory=group)",
        "group",
        new[]
        {
            "DistinguishedName", "GroupCategory", "GroupScope", "Name", "ObjectClass",
            "ObjectGUID", "SamAccountName", "SID"
        },
        IdentityIncludesSamAccountName: true,
        IdentitySamTriesDollarSuffix: false);

    public static readonly AdObjectSchema Computer = new(
        "computer",
        "(objectCategory=computer)",
        "computer",
        new[]
        {
            "DistinguishedName", "DNSHostName", "Enabled", "Name", "ObjectClass", "ObjectGUID",
            "SamAccountName", "SID", "UserPrincipalName"
        },
        IdentityIncludesSamAccountName: true,
        IdentitySamTriesDollarSuffix: true,
        // Managed service accounts derive from computer, but their objectCategory is their
        // own class, so (objectCategory=computer) excludes them on the wire -- RSAT points
        // them at Get-ADServiceAccount instead. Without these, the DN fast path would accept
        // what the same cmdlet's -Filter rejects: the exact inconsistency RequiredClass
        // chain-matching was introduced to eliminate, reintroduced in the other direction.
        DisqualifyingClasses: new[] { "msDS-GroupManagedServiceAccount", "msDS-ManagedServiceAccount" });

    public static readonly AdObjectSchema ServiceAccount = new(
        "serviceAccount",
        // msDS-GroupManagedServiceAccount derives from msDS-ManagedServiceAccount, so the base
        // CLASS matches BOTH the group-managed (derived) and standalone (own class) accounts and
        // nothing else -- the one place a derived class SHOULD match, the inverse of the User row.
        // objectCategory would not do: it holds each MSA's own most-specific category, which
        // differs between gMSA and sMSA.
        "(objectClass=msDS-ManagedServiceAccount)",
        "msDS-ManagedServiceAccount",
        new[]
        {
            "DistinguishedName", "Enabled", "Name", "ObjectClass", "ObjectGUID",
            "SamAccountName", "SID", "UserPrincipalName"
        },
        IdentityIncludesSamAccountName: true,
        IdentitySamTriesDollarSuffix: true);
        // No DisqualifyingClasses: msDS-ManagedServiceAccount is present only on MSAs, so a plain
        // computer/user is rejected by the RequiredClass check alone -- and the wire filter has no
        // exclusion either, so the DN fast path and -Filter agree.

    public static readonly AdObjectSchema AnyObject = new(
        "object",
        BaseFilter: null,
        RequiredClass: null,
        new[] { "DistinguishedName", "Name", "ObjectClass", "ObjectGUID" },
        IdentityIncludesSamAccountName: false,
        IdentitySamTriesDollarSuffix: false);

    /// <summary>
    /// Fine-grained password policies (PSO, objectClass msDS-PasswordSettings), for
    /// Get-ADxFineGrainedPasswordPolicy. Lives only in CN=Password Settings Container,CN=System
    /// under the domain head, so the search base defaults there. Its RSAT display names collide
    /// with the domain-head policy names (MaxPasswordAge etc.) but map to the msDS-* attributes,
    /// so they are carried in AttributeOverrides (the OU StreetAddress pattern) rather than the
    /// global alias table. Identity is by name (a PSO has no sAMAccountName), DN, or GUID.
    /// </summary>
    public static readonly AdObjectSchema FineGrainedPasswordPolicy = new(
        "fineGrainedPasswordPolicy",
        "(objectClass=msDS-PasswordSettings)",
        "msDS-PasswordSettings",
        new[]
        {
            "AppliesTo", "ComplexityEnabled", "DistinguishedName", "LockoutDuration",
            "LockoutObservationWindow", "LockoutThreshold", "MaxPasswordAge", "MinPasswordAge",
            "MinPasswordLength", "Name", "ObjectClass", "ObjectGUID", "PasswordHistoryCount",
            "Precedence", "ReversibleEncryptionEnabled"
        },
        IdentityIncludesSamAccountName: false,
        IdentitySamTriesDollarSuffix: false,
        DisqualifyingClasses: null,
        AttributeOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Precedence"] = "msDS-PasswordSettingsPrecedence",
            ["MinPasswordLength"] = "msDS-MinimumPasswordLength",
            ["MinPasswordAge"] = "msDS-MinimumPasswordAge",
            ["MaxPasswordAge"] = "msDS-MaximumPasswordAge",
            ["PasswordHistoryCount"] = "msDS-PasswordHistoryLength",
            ["LockoutThreshold"] = "msDS-LockoutThreshold",
            ["LockoutDuration"] = "msDS-LockoutDuration",
            ["LockoutObservationWindow"] = "msDS-LockoutObservationWindow",
            ["ComplexityEnabled"] = "msDS-PasswordComplexityEnabled",
            ["ReversibleEncryptionEnabled"] = "msDS-PasswordReversibleEncryptionEnabled",
            ["AppliesTo"] = "msDS-PSOAppliesTo",
        },
        IdentityByName: true,
        DefaultContainerRelativeDn: "CN=Password Settings Container,CN=System");

    /// <summary>
    /// The slim account shape returned by Search-ADxAccount: a mix of users and computers, so
    /// the defaults are the intersection RSAT's ADAccount exposes (verified against a live DC --
    /// RSAT's change-tracking bookkeeping properties, which a read-only module cannot produce,
    /// are correctly absent). Projection-only: no base filter (the cmdlet builds its own scoped
    /// criterion filter) and no identity path.
    /// </summary>
    public static readonly AdObjectSchema Account = new(
        "account",
        BaseFilter: null,
        RequiredClass: null,
        new[]
        {
            "AccountExpirationDate", "DistinguishedName", "Enabled", "LastLogonDate", "LockedOut",
            "Name", "ObjectClass", "ObjectGUID", "PasswordExpired", "PasswordNeverExpires",
            "SamAccountName", "SID", "UserPrincipalName"
        },
        IdentityIncludesSamAccountName: false,
        IdentitySamTriesDollarSuffix: false);

    /// <summary>
    /// Get-ADxOrganizationalUnit: RSAT's Get-ADOrganizationalUnit. Identity is DN or GUID only
    /// (an OU has no sAMAccountName or objectSid), matching RSAT. Two RSAT-specific fidelity
    /// points live in the fields below:
    /// <list type="bullet">
    /// <item><c>Name</c> is the <c>ou</c> attribute, but resolves through the direct-name path
    /// (RSAT's Name for an OU IS its RDN, which is the ou value) so no override is needed for
    /// it -- only <c>StreetAddress</c> genuinely maps to a different LDAP attribute than the
    /// user schema uses.</item>
    /// <item><c>StreetAddress</c> is the LDAP <c>street</c> attribute for OUs, where a user's
    /// is <c>streetAddress</c> -- carried by <see cref="AttributeOverrides"/> so the global
    /// alias table (which maps StreetAddress-&gt;streetAddress) does not silently emit null.</item>
    /// </list>
    /// </summary>
    public static readonly AdObjectSchema OrganizationalUnit = new(
        "organizationalUnit",
        "(objectCategory=organizationalUnit)",
        "organizationalUnit",
        new[]
        {
            "City", "Country", "DistinguishedName", "LinkedGroupPolicyObjects", "ManagedBy",
            "Name", "ObjectClass", "ObjectGUID", "PostalCode", "State", "StreetAddress"
        },
        IdentityIncludesSamAccountName: false,
        IdentitySamTriesDollarSuffix: false,
        DisqualifyingClasses: null,
        AttributeOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["StreetAddress"] = "street",
        });

    /// <summary>
    /// The output shape of Get-ADxGroupMember: a member can be a user, computer, group or
    /// contact, so the defaults are the RSAT ADPrincipal set -- the intersection that is
    /// meaningful for all of them. Never used for identity resolution (no base filter; the
    /// member search filter does the constraining).
    /// </summary>
    public static readonly AdObjectSchema Principal = new(
        "principal",
        BaseFilter: null,
        RequiredClass: null,
        new[] { "DistinguishedName", "Name", "ObjectClass", "ObjectGUID", "SamAccountName", "SID" },
        IdentityIncludesSamAccountName: false,
        IdentitySamTriesDollarSuffix: false);

    /// <summary>
    /// Identity resolution for Get-ADxPrincipalGroupMembership. A principal whose group
    /// memberships you can ask for is a user, computer, group or service account, so no single
    /// objectClass constrains it and no type check is applied -- resolution accepts whatever
    /// the identity names, then requires only that it carry an objectSid (is a security
    /// principal). Every identity form RSAT's Get-ADPrincipalGroupMembership takes is accepted:
    /// DN, objectGUID, SID, or sAMAccountName, with the computer '$' retry since a computer is
    /// a principal too. This drives resolution only; the results are groups, projected through
    /// <see cref="Group"/>.
    /// </summary>
    public static readonly AdObjectSchema SecurityPrincipal = new(
        "principal",
        BaseFilter: null,
        RequiredClass: null,
        DefaultProperties: Array.Empty<string>(),
        IdentityIncludesSamAccountName: true,
        IdentitySamTriesDollarSuffix: true);

    /// <summary>
    /// Does this object's <c>objectClass</c> chain make it this type? The one implementation
    /// both identity paths share, so the preset cmdlets and the membership cmdlets cannot
    /// drift into disagreeing about what counts as a user.
    /// </summary>
    public bool MatchesType(IReadOnlyList<string> objectClasses)
    {
        if (RequiredClass is null) return true;
        if (objectClasses is null || objectClasses.Count == 0) return false;

        if (DisqualifyingClasses is not null)
        {
            foreach (var disqualifier in DisqualifyingClasses)
            {
                foreach (var actual in objectClasses)
                {
                    if (string.Equals(actual, disqualifier, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
        }

        foreach (var actual in objectClasses)
        {
            if (string.Equals(actual, RequiredClass, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
