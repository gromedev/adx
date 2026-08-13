using System.Management.Automation;
using System.Text.RegularExpressions;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Base;

/// <summary>How an -Identity value was recognised.</summary>
internal enum AdIdentityKind
{
    DistinguishedName,
    ObjectGuid,
    Sid,
    SamAccountName,

    /// <summary>
    /// Resolution by the object's <c>name</c> (cn) attribute, for types that have no
    /// sAMAccountName or objectSid -- fine-grained password policies (PSOs), whose RSAT
    /// cmdlet takes the policy name as its identity.
    /// </summary>
    Name
}

/// <summary>
/// Classifies an <c>-Identity</c> argument and produces the corresponding lookup. Detection
/// order, first match wins: DN → GUID (D/N string formats only) → SID → sAMAccountName.
/// The order matters: every one of these is technically a legal sAMAccountName, so the more
/// structured forms must be tested first, and the fall-through to sAMAccountName only exists
/// for object types whose RSAT cmdlet accepts it (Get-ADObject takes DN or GUID only).
/// </summary>
internal static class AdIdentityResolver
{
    private static readonly Regex SidPattern = new(@"^S-\d+-\d+(-\d+)+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Classify a raw -Identity argument. Throws <see cref="AdFilterTranslationException"/>
    /// for shapes that cannot be an identity at all.
    /// </summary>
    public static (AdIdentityKind Kind, object Value) Classify(object identity, AdObjectSchema schema)
    {
        var value = identity is PSObject pso ? pso.BaseObject : identity;

        switch (value)
        {
            case Guid guid:
                return (AdIdentityKind.ObjectGuid, guid);

            case ADxSecurityIdentifier sid:
                return ClassifySid(sid.Value, schema);

            case string s:
                return ClassifyString(s, schema);

            // RSAT's own SID output type, duck-typed by NAME: constructing the real
            // System.Security.Principal.SecurityIdentifier is Windows-only and referencing
            // its assembly would add a dependency, but on Windows a drop-in script passes
            // $rsatUser.SID straight in and RSAT accepts it. ToString() is its SDDL form.
            case not null when value.GetType().FullName == "System.Security.Principal.SecurityIdentifier":
                return ClassifySid(value.ToString()!, schema);

            default:
                // Piped objects arrive here WHOLE: -Identity is typed object, and PowerShell
                // attempts ValueFromPipeline binding before ValueFromPipelineByPropertyName --
                // binding anything to object always succeeds, so the DistinguishedName-alias
                // path never runs for `Get-ADxGroup ... | Get-ADxGroupMember`. Accept any
                // object carrying a string DistinguishedName (ADx output, RSAT's
                // ADUser/ADGroup, [pscustomobject]) by classifying that DN.
                if (TryGetDistinguishedName(identity) is { Length: > 0 } dn)
                    return ClassifyString(dn, schema);

                throw new AdFilterTranslationException(
                    $"-Identity cannot be a {value?.GetType().Name ?? "null"}. Pass a distinguished name, " +
                    $"objectGUID{(schema.IdentityIncludesSamAccountName ? ", SID, or sAMAccountName" : " or SID")}, " +
                    "or pipe an object with a DistinguishedName property.");
        }
    }

    /// <summary>
    /// A TYPED SID identity (ADxSecurityIdentifier or the real SecurityIdentifier). Accepted
    /// only for security-principal types, mirroring the string-form gate in
    /// <see cref="ClassifyString"/>: RSAT's Get-ADObject/-ADOrganizationalUnit reject SID
    /// identities in any spelling.
    /// </summary>
    private static (AdIdentityKind Kind, object Value) ClassifySid(string sddl, AdObjectSchema schema)
    {
        if (schema.IdentityIncludesSamAccountName)
            return (AdIdentityKind.Sid, sddl);

        throw new AdFilterTranslationException(
            $"Get-ADx{char.ToUpperInvariant(schema.TypeLabel[0])}{schema.TypeLabel.Substring(1)} does not " +
            "accept a SID as -Identity, matching its RSAT counterpart. Use a distinguished name or objectGUID.");
    }

    private static string? TryGetDistinguishedName(object identity)
    {
        // PSObject.Properties surfaces both note properties (ADx/pscustomobject output) and
        // adapted .NET properties (RSAT's ADGroup/ADUser), so one lookup covers every shape.
        var pso = identity as PSObject ?? PSObject.AsPSObject(identity);
        return pso.Properties["DistinguishedName"]?.Value switch
        {
            string s => s,
            PSObject inner when inner.BaseObject is string s => s,
            _ => null
        };
    }

    private static (AdIdentityKind Kind, object Value) ClassifyString(string identity, AdObjectSchema schema)
    {
        var trimmed = identity.Trim();
        if (trimmed.Length == 0)
            throw new AdFilterTranslationException("-Identity is empty.");

        // DN: at least one type=value RDN. ParseDn handles escaped commas, so
        // "CN=Doe\, John,OU=Users,DC=x" classifies correctly.
        if (trimmed.Contains('='))
        {
            var rdns = LdapConvert.ParseDn(trimmed);
            if (rdns.Count > 0 && rdns.All(r => r.Type.Length > 0))
                return (AdIdentityKind.DistinguishedName, trimmed);
        }

        // GUID: D and N formats only, per RSAT. TryParse would also take {braced} and
        // (parenthesized) forms, which RSAT treats as not-a-GUID.
        if (Guid.TryParseExact(trimmed, "D", out var guid) || Guid.TryParseExact(trimmed, "N", out guid))
            return (AdIdentityKind.ObjectGuid, guid);

        // Gated by the schema BEFORE the pattern: RSAT's non-principal cmdlets (Get-ADObject,
        // Get-ADOrganizationalUnit) accept DN/GUID only, and a SID string reaching them must
        // fall through to the tailored rejection below rather than classify as a SID lookup
        // the counterpart would refuse. For PSOs the fall-through is IdentityByName -- a
        // policy legitimately named like a SID stays resolvable by name.
        if (schema.IdentityIncludesSamAccountName && SidPattern.IsMatch(trimmed))
            return (AdIdentityKind.Sid, trimmed);

        if (schema.IdentityIncludesSamAccountName)
            return (AdIdentityKind.SamAccountName, trimmed);

        // Types with no sAMAccountName but a unique name within their container (PSOs).
        if (schema.IdentityByName)
            return (AdIdentityKind.Name, trimmed);

        throw new AdFilterTranslationException(
            $"'{identity}' is not a distinguished name or objectGUID. Get-ADx{char.ToUpperInvariant(schema.TypeLabel[0])}{schema.TypeLabel.Substring(1)} " +
            "accepts only those identity forms, matching its RSAT counterpart.");
    }

    /// <summary>
    /// The LDAP filter for an identity lookup. DN identities normally skip search entirely
    /// via a base-scope read at the DN itself; the DistinguishedName case here serves the
    /// scoped path (-SearchBase given), where an equality filter inside the requested subtree
    /// is what makes DN identities honour the same scope every other identity kind does.
    /// Composed with the schema's base filter by the caller.
    /// </summary>
    public static AdFilterNode BuildLookupFilter(AdIdentityKind kind, object value)
    {
        switch (kind)
        {
            case AdIdentityKind.DistinguishedName:
                return new AdFilterEquality("distinguishedName",
                    LdapAssertionValue.Exact((string)value));

            case AdIdentityKind.ObjectGuid:
                return new AdFilterEquality("objectGUID",
                    LdapAssertionValue.Binary(((Guid)value).ToByteArray()));

            case AdIdentityKind.Sid:
            {
                var sid = LdapConvert.SddlToSid((string)value)
                    ?? throw new AdFilterTranslationException($"'{value}' is not a valid SID.");
                return new AdFilterEquality("objectSid", LdapAssertionValue.Binary(sid));
            }

            case AdIdentityKind.SamAccountName:
                return new AdFilterEquality("sAMAccountName",
                    LdapAssertionValue.Exact((string)value));

            case AdIdentityKind.Name:
                return new AdFilterEquality("name", LdapAssertionValue.Exact((string)value));

            default:
                throw new InvalidOperationException($"No lookup filter for identity kind {kind}.");
        }
    }
}
