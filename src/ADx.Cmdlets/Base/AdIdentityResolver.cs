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
    SamAccountName
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
                return (AdIdentityKind.Sid, sid.Value);

            case string s:
                return ClassifyString(s, schema);

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

        if (SidPattern.IsMatch(trimmed))
            return (AdIdentityKind.Sid, trimmed);

        if (!schema.IdentityIncludesSamAccountName)
            throw new AdFilterTranslationException(
                $"'{identity}' is not a distinguished name or objectGUID. Get-ADx{char.ToUpperInvariant(schema.TypeLabel[0])}{schema.TypeLabel.Substring(1)} " +
                "accepts only those identity forms, matching its RSAT counterpart.");

        return (AdIdentityKind.SamAccountName, trimmed);
    }

    /// <summary>
    /// The LDAP filter for a non-DN identity lookup (DN identities skip search entirely via
    /// a base-scope read). Composed with the schema's base filter by the caller.
    /// </summary>
    public static AdFilterNode BuildLookupFilter(AdIdentityKind kind, object value)
    {
        switch (kind)
        {
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

            default:
                throw new InvalidOperationException($"No lookup filter for identity kind {kind}.");
        }
    }
}
