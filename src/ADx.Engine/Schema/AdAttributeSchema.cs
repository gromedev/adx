namespace ADx.Engine.Ldap;

/// <summary>
/// How an LDAP attribute's wire value must be marshalled, in both directions: filter
/// assertions (typed value -> LDAP text/binary) and output projection (LDAP text/binary ->
/// .NET type). Driving both from one table is the point -- a separate ad hoc conversion in
/// the filter translator and the projector is exactly how "filtering on LastLogonDate works
/// but the emitted column is wrong" bugs happen.
/// </summary>
public enum AdAttributeSyntax
{
    String,
    Integer,
    Boolean,
    GeneralizedTime,
    FileTime,

    /// <summary>
    /// A duration stored as a NEGATIVE count of 100ns ticks (maxPwdAge, lockoutDuration...).
    /// Not FileTime: those are points in time and their converter treats any value &lt;= 0 as
    /// a "never" sentinel, which would silently null every interval attribute. 0 means "none"
    /// and <see cref="long.MinValue"/> means "never" here.
    /// </summary>
    Interval,
    Sid,
    Guid,
    Dn,
    Binary
}

/// <summary>
/// The syntax table for well-known Active Directory attributes, keyed by LDAP attribute name.
/// <para>
/// This is the table promoted out of <c>ADxCmdletBase</c>'s three private
/// <c>HashSet&lt;string&gt;</c> fields (GeneralizedTime/FileTime/Integer attributes), which
/// only covered what the M2-era projector happened to touch. Living here instead means the
/// filter translator (marshalling a typed value into a filter assertion) and the output
/// projector (marshalling a wire value into a .NET type) share one answer for "what kind of
/// value is <c>pwdLastSet</c>" rather than risking two.
/// </para>
/// <para>
/// The full RSAT&lt;-&gt;LDAP display-name table lives with each preset cmdlet (M3/M4), not
/// here -- this table only needs to answer "what syntax does this LDAP attribute have",
/// which does not require knowing every RSAT alias up front.
/// </para>
/// </summary>
public static class AdAttributeSchema
{
    private static readonly Dictionary<string, AdAttributeSyntax> Syntax =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // GeneralizedTime (RFC 4517): AD's replication-metadata timestamps.
            ["whenCreated"] = AdAttributeSyntax.GeneralizedTime,
            ["whenChanged"] = AdAttributeSyntax.GeneralizedTime,
            ["createTimeStamp"] = AdAttributeSyntax.GeneralizedTime,
            ["modifyTimeStamp"] = AdAttributeSyntax.GeneralizedTime,

            // FileTime: Windows 100ns-since-1601 timestamps, compared as raw 64-bit integers.
            ["lastLogonTimestamp"] = AdAttributeSyntax.FileTime,
            ["lastLogon"] = AdAttributeSyntax.FileTime,
            ["lastLogoff"] = AdAttributeSyntax.FileTime,
            ["pwdLastSet"] = AdAttributeSyntax.FileTime,
            ["accountExpires"] = AdAttributeSyntax.FileTime,
            ["badPasswordTime"] = AdAttributeSyntax.FileTime,
            ["lockoutTime"] = AdAttributeSyntax.FileTime,
            ["msDS-UserPasswordExpiryTimeComputed"] = AdAttributeSyntax.FileTime,

            // Interval: durations stored as negative 100ns tick counts on the domain head.
            // Registering these as FileTime would silently null them (its <= 0 sentinel check).
            ["maxPwdAge"] = AdAttributeSyntax.Interval,
            ["minPwdAge"] = AdAttributeSyntax.Interval,
            ["lockoutDuration"] = AdAttributeSyntax.Interval,
            ["lockOutObservationWindow"] = AdAttributeSyntax.Interval,

            // Fine-grained password policy (PSO) durations: the msDS-* equivalents, same
            // negative-100ns-interval syntax as the domain-head family above.
            ["msDS-MinimumPasswordAge"] = AdAttributeSyntax.Interval,
            ["msDS-MaximumPasswordAge"] = AdAttributeSyntax.Interval,
            ["msDS-LockoutDuration"] = AdAttributeSyntax.Interval,
            ["msDS-LockoutObservationWindow"] = AdAttributeSyntax.Interval,

            // Integer: plain numeric attributes, including bit-packed flag fields whose
            // decoding into named booleans is the projector's job, not the schema's.
            ["userAccountControl"] = AdAttributeSyntax.Integer,
            ["groupType"] = AdAttributeSyntax.Integer,
            ["primaryGroupID"] = AdAttributeSyntax.Integer,
            ["primaryGroupToken"] = AdAttributeSyntax.Integer,
            ["adminCount"] = AdAttributeSyntax.Integer,
            ["logonCount"] = AdAttributeSyntax.Integer,
            ["badPwdCount"] = AdAttributeSyntax.Integer,
            ["sAMAccountType"] = AdAttributeSyntax.Integer,
            ["instanceType"] = AdAttributeSyntax.Integer,
            ["uSNCreated"] = AdAttributeSyntax.Integer,
            ["uSNChanged"] = AdAttributeSyntax.Integer,
            ["msDS-SupportedEncryptionTypes"] = AdAttributeSyntax.Integer,
            ["gPOptions"] = AdAttributeSyntax.Integer,
            ["minPwdLength"] = AdAttributeSyntax.Integer,
            ["pwdHistoryLength"] = AdAttributeSyntax.Integer,
            ["lockoutThreshold"] = AdAttributeSyntax.Integer,
            ["pwdProperties"] = AdAttributeSyntax.Integer,
            ["msDS-PasswordSettingsPrecedence"] = AdAttributeSyntax.Integer,
            ["msDS-MinimumPasswordLength"] = AdAttributeSyntax.Integer,
            ["msDS-PasswordHistoryLength"] = AdAttributeSyntax.Integer,
            ["msDS-LockoutThreshold"] = AdAttributeSyntax.Integer,
            ["msDS-ManagedPasswordInterval"] = AdAttributeSyntax.Integer,
            ["msDS-Behavior-Version"] = AdAttributeSyntax.Integer,
            ["systemFlags"] = AdAttributeSyntax.Integer,
            ["options"] = AdAttributeSyntax.Integer,
            ["ms-DS-MachineAccountQuota"] = AdAttributeSyntax.Integer,
            ["nTMixedDomain"] = AdAttributeSyntax.Integer,

            // Boolean.
            ["isDeleted"] = AdAttributeSyntax.Boolean,
            ["isCriticalSystemObject"] = AdAttributeSyntax.Boolean,
            // PSO complexity/reversible-encryption are their OWN boolean attributes, not bits of
            // a pwdProperties field like the domain default policy.
            ["msDS-PasswordComplexityEnabled"] = AdAttributeSyntax.Boolean,
            ["msDS-PasswordReversibleEncryptionEnabled"] = AdAttributeSyntax.Boolean,

            // Sid: binary objectSid/sIDHistory, decoded/encoded by LdapConvert's hand-rolled
            // SDDL codec rather than the Windows-only SecurityIdentifier.
            ["objectSid"] = AdAttributeSyntax.Sid,
            ["sIDHistory"] = AdAttributeSyntax.Sid,

            // Guid: binary objectGUID, in .NET's native Guid byte order.
            ["objectGUID"] = AdAttributeSyntax.Guid,
            ["invocationId"] = AdAttributeSyntax.Guid,

            // Dn: values that are themselves distinguished names, not free text.
            ["distinguishedName"] = AdAttributeSyntax.Dn,
            ["objectCategory"] = AdAttributeSyntax.Dn,
            ["manager"] = AdAttributeSyntax.Dn,
            ["managedBy"] = AdAttributeSyntax.Dn,
            ["memberOf"] = AdAttributeSyntax.Dn,
            ["member"] = AdAttributeSyntax.Dn,
            ["fSMORoleOwner"] = AdAttributeSyntax.Dn,
            ["trustParent"] = AdAttributeSyntax.Dn,
            ["serverReference"] = AdAttributeSyntax.Dn,
            ["msDS-AssignedAuthNPolicy"] = AdAttributeSyntax.Dn,
            ["msDS-AssignedAuthNPolicySilo"] = AdAttributeSyntax.Dn,
            ["msDS-AllowedToActOnBehalfOfOtherIdentity"] = AdAttributeSyntax.Binary,
            ["msDS-PSOAppliesTo"] = AdAttributeSyntax.Dn,
            ["msDS-HostServiceAccountBL"] = AdAttributeSyntax.Dn,

            // Binary: opaque byte blobs with no textual form.
            ["userCertificate"] = AdAttributeSyntax.Binary,
            ["nTSecurityDescriptor"] = AdAttributeSyntax.Binary,
            // The gMSA password-retrieval ACL: a security descriptor, surfaced raw as bytes;
            // its friendly name PrincipalsAllowedToRetrieveManagedPassword is declared unsupported.
            ["msDS-GroupMSAMembership"] = AdAttributeSyntax.Binary,
        };

    /// <summary>
    /// The syntax of an LDAP attribute. Attributes with a <c>;range=</c> suffix resolve as
    /// their base name -- <see cref="LdapEntry.TryParseRangeOption"/> is the same parser the
    /// projector uses, so the two agree on what "the base name" means.
    /// </summary>
    public static AdAttributeSyntax SyntaxOf(string ldapAttributeName)
    {
        var name = LdapEntry.TryParseRangeOption(ldapAttributeName, out var baseName, out _, out _, out _)
            ? baseName
            : ldapAttributeName;

        return Syntax.TryGetValue(name, out var syntax) ? syntax : AdAttributeSyntax.String;
    }

    public static bool IsKnownAttribute(string ldapAttributeName) =>
        Syntax.ContainsKey(
            LdapEntry.TryParseRangeOption(ldapAttributeName, out var baseName, out _, out _, out _)
                ? baseName
                : ldapAttributeName);

    /// <summary>
    /// RSAT display name -&gt; LDAP attribute name, for every attribute in the plan's Common/
    /// User/Group/Computer property tables where the two names differ by more than case (the
    /// "classic trap" cases -- <c>EmailAddress</c>/<c>mail</c>, <c>Surname</c>/<c>sn</c>,
    /// <c>Members</c>/<c>member</c>). Attributes named identically to their LDAP attribute
    /// (allowing for case, e.g. <c>Name</c>/<c>name</c>, <c>SamAccountName</c>/
    /// <c>sAMAccountName</c>) need no entry here -- <see cref="KnownLdapAttributeNames"/>
    /// already recognises the LDAP name directly, case-insensitively.
    /// <para>
    /// This does not attempt to be exhaustive of every attribute AD ships -- the schema is
    /// extensible and no fixed table can enumerate every custom attribute a real directory
    /// might have. It is the curated set the plan's property tables name explicitly, plus the
    /// <c>-AllowUnknownProperty</c> escape hatch (implemented by the filter translator, not
    /// here) for anything outside it.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> RsatAliasToLdap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Common
            ["SID"] = "objectSid",
            ["ObjectSid"] = "objectSid",
            ["DisplayName"] = "displayName",
            ["Created"] = "whenCreated",
            ["Modified"] = "whenChanged",
            ["Deleted"] = "isDeleted",
            ["CanonicalName"] = "canonicalName",

            // User
            ["Surname"] = "sn",
            ["EmailAddress"] = "mail",
            ["Office"] = "physicalDeliveryOfficeName",
            ["OfficePhone"] = "telephoneNumber",
            ["MobilePhone"] = "mobile",
            ["HomePhone"] = "homePhone",
            ["Fax"] = "facsimileTelephoneNumber",
            ["City"] = "l",
            ["State"] = "st",
            ["Country"] = "c",
            ["POBox"] = "postOfficeBox",
            ["Organization"] = "o",
            ["OtherName"] = "middleName",
            ["ServicePrincipalNames"] = "servicePrincipalName",
            ["LastLogonDate"] = "lastLogonTimestamp",
            ["AccountExpirationDate"] = "accountExpires",
            ["BadLogonCount"] = "badPwdCount",
            ["LastBadPasswordAttempt"] = "badPasswordTime",
            ["PasswordLastSet"] = "pwdLastSet",
            ["AccountLockoutTime"] = "lockoutTime",
            ["Certificates"] = "userCertificate",
            ["AuthenticationPolicy"] = "msDS-AssignedAuthNPolicy",
            ["AuthenticationPolicySilo"] = "msDS-AssignedAuthNPolicySilo",

            // Group
            ["Members"] = "member",
            ["HomePage"] = "wWWHomePage",

            // Domain head / password policy (LockoutDuration and LockoutObservationWindow
            // differ from their attributes only by case, so they need no entry).
            ["MaxPasswordAge"] = "maxPwdAge",
            ["MinPasswordAge"] = "minPwdAge",
            ["MinPasswordLength"] = "minPwdLength",
            ["PasswordHistoryCount"] = "pwdHistoryLength",

            // Service accounts. The PSO display names (Precedence, ComplexityEnabled, ...) are
            // NOT aliased here -- they collide with the domain-head names above and are carried
            // per-type in AdObjectSchema.FineGrainedPasswordPolicy.AttributeOverrides instead.
            ["ManagedPasswordIntervalInDays"] = "msDS-ManagedPasswordInterval",
            ["HostComputers"] = "msDS-HostServiceAccountBL",
        };

    /// <summary>
    /// Every LDAP attribute name the plan's property tables mention, whether or not it needs an
    /// alias above -- the direct-name half of "key the table by both the RSAT name and the LDAP
    /// name". Attributes already in <see cref="Syntax"/> (because they need non-default
    /// marshalling) are included automatically; this adds the plain-<see cref="AdAttributeSyntax.String"/>
    /// ones that would otherwise never be registered anywhere.
    /// </summary>
    private static readonly HashSet<string> KnownLdapAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "distinguishedName", "name", "cn", "objectClass", "objectGUID", "objectCategory",
        "objectSid", "description", "displayName", "whenCreated", "whenChanged",
        "uSNCreated", "uSNChanged", "isDeleted", "canonicalName", "createTimeStamp",
        "modifyTimeStamp",

        "sAMAccountName", "userPrincipalName", "givenName", "sn", "initials", "mail",
        "userAccountControl", "lockoutTime", "pwdLastSet", "lastLogonTimestamp", "lastLogon",
        "lastLogoff", "sAMAccountType", "instanceType", "isCriticalSystemObject",
        "accountExpires", "badPwdCount", "badPasswordTime", "logonCount", "memberOf",
        "primaryGroupID", "manager", "department", "division", "company", "title",
        "physicalDeliveryOfficeName", "telephoneNumber", "mobile", "homePhone",
        "facsimileTelephoneNumber", "streetAddress", "l", "st", "postalCode", "c",
        "postOfficeBox", "employeeID", "employeeNumber", "o", "middleName", "homeDirectory",
        "homeDrive", "profilePath", "scriptPath", "servicePrincipalName", "proxyAddresses",
        "sIDHistory", "userCertificate", "adminCount", "msDS-SupportedEncryptionTypes",
        "msDS-AssignedAuthNPolicy", "msDS-AssignedAuthNPolicySilo",
        "msDS-AllowedToActOnBehalfOfOtherIdentity", "msDS-UserPasswordExpiryTimeComputed",
        "msDS-User-Account-Control-Computed", "nTSecurityDescriptor",

        "groupType", "member", "managedBy", "wWWHomePage", "info", "primaryGroupToken",

        "dNSHostName", "location", "operatingSystem", "operatingSystemVersion",
        "operatingSystemServicePack", "operatingSystemHotfix",

        // Organizational units.
        "ou", "gPLink", "gPOptions", "street",

        // Domain/forest topology: the domain head, the Partitions crossRefs, and the
        // config-partition server/nTDSDSA objects the topology cmdlets read.
        "maxPwdAge", "minPwdAge", "lockoutDuration", "lockOutObservationWindow",
        "minPwdLength", "pwdHistoryLength", "lockoutThreshold", "pwdProperties",
        "fSMORoleOwner", "msDS-Behavior-Version", "nETBIOSName", "dnsRoot", "trustParent",
        "wellKnownObjects", "otherWellKnownObjects", "systemFlags", "options",
        "uPNSuffixes", "msDS-SPNSuffixes", "msDS-AllowedDNSSuffixes", "serverReference",
        "invocationId", "ms-DS-MachineAccountQuota", "nTMixedDomain",

        // Fine-grained password policies (PSO).
        "msDS-PasswordSettingsPrecedence", "msDS-MinimumPasswordLength",
        "msDS-MinimumPasswordAge", "msDS-MaximumPasswordAge", "msDS-PasswordHistoryLength",
        "msDS-LockoutThreshold", "msDS-LockoutDuration", "msDS-LockoutObservationWindow",
        "msDS-PasswordComplexityEnabled", "msDS-PasswordReversibleEncryptionEnabled",
        "msDS-PSOAppliesTo",

        // Service accounts.
        "msDS-ManagedPasswordInterval", "msDS-HostServiceAccountBL", "msDS-GroupMSAMembership",
    };

    private static readonly Dictionary<string, string> LdapToRsatAlias = BuildReverseAliasMap();

    private static Dictionary<string, string> BuildReverseAliasMap()
    {
        // Inverted from RsatAliasToLdap. Where several RSAT names share one attribute
        // (SID/ObjectSid -> objectSid), first registration wins, so declaration order in
        // RsatAliasToLdap decides the canonical RSAT name.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rsat, ldap) in RsatAliasToLdap)
        {
            if (!map.ContainsKey(ldap)) map[ldap] = rsat;
        }
        return map;
    }

    /// <summary>
    /// The curated RSAT display name for an LDAP attribute, when one exists and differs from
    /// the attribute name (mail -&gt; EmailAddress, sn -&gt; Surname). Used by the output
    /// projector to emit both names when the caller asked in LDAP terms.
    /// </summary>
    public static bool TryGetRsatNameForLdapAttribute(string ldapName, out string rsatName) =>
        LdapToRsatAlias.TryGetValue(ldapName, out rsatName!);

    /// <summary>
    /// Resolve an RSAT display name or LDAP attribute name to its LDAP attribute name in
    /// canonical casing (<c>SamAccountName</c> -&gt; <c>sAMAccountName</c>). Case-insensitive
    /// on both the alias table and the direct-name set; canonical casing matters only for
    /// deterministic emitted filters and readable output, since LDAP itself is
    /// case-insensitive. Returns false for anything not recognised -- callers decide whether
    /// that is a terminating error or, under <c>-AllowUnknownProperty</c>, a literal
    /// pass-through.
    /// </summary>
    public static bool TryResolveAttributeName(string name, out string ldapName)
    {
        if (RsatAliasToLdap.TryGetValue(name, out var aliased))
        {
            ldapName = aliased;
            return true;
        }

        // HashSet.TryGetValue hands back the STORED element, i.e. the canonical casing.
        if (KnownLdapAttributeNames.TryGetValue(name, out var canonical))
        {
            ldapName = canonical;
            return true;
        }

        // Safety net: anything with a syntax entry is known even if the name set missed it.
        if (Syntax.ContainsKey(name))
        {
            ldapName = name;
            return true;
        }

        ldapName = name;
        return false;
    }
}
