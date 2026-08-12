using System.DirectoryServices.Protocols;
using System.Management.Automation;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Base;

/// <summary>
/// Base for the on-prem Active Directory (<c>ADx</c>) cmdlets.
/// <para>
/// Derives from <see cref="ADxCmdletCore"/> for cancellation, disposal and message buffering.
/// ADx deliberately shares no assembly with the Graph module: two modules shipping different
/// versions of one common library into the same PowerShell session is the diamond problem,
/// where whichever loads first wins and the other can fail at JIT time. The ~55 lines in
/// <see cref="ADxCmdletCore"/> are duplicated rather than shared for exactly that reason.
/// </para>
/// </summary>
public abstract class ADxCmdletBase : ADxCmdletCore
{
    private ADxLdapClient? _client;

    /// <summary>
    /// Domain controller or, preferably, the full DNS domain name. Defaults to
    /// <c>USERDNSDOMAIN</c>.
    /// </summary>
    [Parameter]
    [Alias("DomainController", "DC")]
    public string? Server { get; set; }

    /// <summary>389 plain, 636 LDAPS, 3268/3269 Global Catalog.</summary>
    [Parameter]
    [ValidateRange(1, 65535)]
    public int Port { get; set; }

    [Parameter]
    public SwitchParameter UseSsl { get; set; }

    [Parameter]
    [Credential]
    public PSCredential? Credential { get; set; }

    [Parameter]
    [ValidateSet("Negotiate", "Kerberos", "Basic", "Anonymous")]
    [ArgumentCompleter(typeof(LdapAuthTypeCompleter))]
    public string AuthType { get; set; } = "Negotiate";

    /// <summary>
    /// Per-search timeout in seconds. Default 110, just under AD's <c>MaxQueryDuration</c>
    /// of 120 so the client gives up marginally before the server does.
    /// </summary>
    [Parameter]
    [ValidateRange(1, 3600)]
    public int SearchTimeout { get; set; } = 110;

    /// <summary>
    /// Follow referrals into other domains. Off by default: chasing them silently widens
    /// the search beyond what was asked for.
    /// </summary>
    [Parameter]
    public SwitchParameter ChaseReferrals { get; set; }

    /// <summary>
    /// The port this cmdlet will actually connect on, resolving the -Port / -UseSsl defaults.
    /// </summary>
    protected int EffectivePort => Port > 0 ? Port : (UseSsl.IsPresent ? 636 : 389);

    /// <summary>
    /// True when bound to a Global Catalog (3268/3269). A GC answers a subtree search from the
    /// forest-root naming context across every domain in the forest, because each domain NC is
    /// namespace-subordinate to the root -- so a membership in another same-forest domain is
    /// RETURNED by a GC search, where a plain 389/636 bind (one hosted partition) would not.
    /// </summary>
    protected bool IsGlobalCatalog => EffectivePort is 3268 or 3269;

    protected LdapClientOptions BuildOptions() => new()
    {
        Server = Server,
        Port = Port > 0 ? Port : (UseSsl.IsPresent ? 636 : 389),
        UseSsl = UseSsl.IsPresent,
        AuthMode = Enum.TryParse<LdapAuthMode>(AuthType, ignoreCase: true, out var mode)
            ? mode
            : LdapAuthMode.Negotiate,
        Credential = Credential?.GetNetworkCredential(),
        SearchTimeoutSeconds = SearchTimeout,
        ChaseReferrals = ChaseReferrals.IsPresent
    };

    /// <summary>
    /// Connect, bind and read RootDSE, caching the connection for the cmdlet's lifetime.
    /// Every failure mode gets a distinct errorId so it can be diagnosed without a stack trace.
    /// </summary>
    protected ADxLdapClient GetConnection()
    {
        if (_client is not null) return _client;

        try
        {
            _client = ADxLdapClient.ConnectAsync(
                    BuildOptions(),
                    verbose: EnqueueVerbose,
                    warning: EnqueueWarning,
                    cancellationToken: CancellationToken)
                .GetAwaiter().GetResult();
        }
        catch (LdapRuntimeMissingException ex)
        {
            DrainMessages();
            ThrowTerminatingError(new ErrorRecord(
                ex, "LdapRuntimeMissing", ErrorCategory.NotInstalled, Server));
            return null!;
        }
        catch (InvalidOperationException ex)
        {
            DrainMessages();
            ThrowTerminatingError(new ErrorRecord(
                ex, "NoDomainController", ErrorCategory.InvalidArgument, Server));
            return null!;
        }
        catch (LdapException ex)
        {
            DrainMessages();
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(DescribeLdapFailure(ex), ex),
                ex.ErrorCode == LdapResultCodes.InvalidCredentials ? "LdapBindFailed" : "LdapConnectionFailed",
                MapResultCodeToCategory(ex.ErrorCode),
                Server));
            return null!;
        }

        DrainMessages();

        if (!_client.RootDse.IsActiveDirectory)
        {
            WriteWarning(
                $"'{_client.ConnectedServer}' responded but reports no defaultNamingContext, so it is " +
                "not an Active Directory domain controller. AD-specific features (range retrieval, " +
                "primaryGroupID, DirSync) are unavailable.");
        }

        return _client;
    }

    /// <summary>
    /// Resolve the search base: the caller's value if given, otherwise the domain's
    /// defaultNamingContext from RootDSE.
    /// <para>
    /// This replaces <c>ActiveDirectory.Domain.GetCurrentDomain()</c>, which is Windows-only
    /// and would make the collector unusable on Linux and macOS.
    /// </para>
    /// </summary>
    protected string ResolveSearchBase(string? searchBase)
    {
        if (!string.IsNullOrWhiteSpace(searchBase)) return searchBase.Trim();

        var defaultContext = GetConnection().RootDse.DefaultNamingContext;
        if (!string.IsNullOrWhiteSpace(defaultContext)) return defaultContext;

        ThrowTerminatingError(new ErrorRecord(
            new InvalidOperationException(
                "No -SearchBase given and the server did not publish a defaultNamingContext. " +
                "Specify -SearchBase explicitly (for example 'DC=corp,DC=contoso,DC=com')."),
            "NoDefaultNamingContext", ErrorCategory.InvalidArgument, Server));
        return null!;
    }

    /// <summary>
    /// Map an LDAP failure onto an ErrorRecord. Returns true when handled.
    /// </summary>
    protected bool WriteLdapError(Exception ex, object? target)
    {
        DrainMessages();

        switch (ex)
        {
            case DirectoryOperationException dex:
                var code = dex.Response is null ? 0 : (int)dex.Response.ResultCode;
                WriteError(new ErrorRecord(
                    new InvalidOperationException(DescribeResultCode(code, dex.Message), dex),
                    $"Ldap{dex.Response?.ResultCode ?? ResultCode.OperationsError}",
                    MapResultCodeToCategory(code), target));
                return true;

            case LdapException lex:
                WriteError(new ErrorRecord(
                    new InvalidOperationException(DescribeLdapFailure(lex), lex),
                    "LdapError", MapResultCodeToCategory(lex.ErrorCode), target));
                return true;

            case LdapRuntimeMissingException rex:
                WriteError(new ErrorRecord(rex, "LdapRuntimeMissing", ErrorCategory.NotInstalled, target));
                return true;

            default:
                return false;
        }
    }

    protected static ErrorCategory MapResultCodeToCategory(int resultCode) => resultCode switch
    {
        LdapResultCodes.InvalidCredentials => ErrorCategory.AuthenticationError,
        LdapResultCodes.InappropriateAuthentication => ErrorCategory.AuthenticationError,
        LdapResultCodes.InsufficientAccessRights => ErrorCategory.PermissionDenied,
        LdapResultCodes.NoSuchObject => ErrorCategory.ObjectNotFound,
        LdapResultCodes.NoSuchAttribute => ErrorCategory.ObjectNotFound,
        LdapResultCodes.SizeLimitExceeded => ErrorCategory.LimitsExceeded,
        LdapResultCodes.TimeLimitExceeded => ErrorCategory.LimitsExceeded,
        LdapResultCodes.AdminLimitExceeded => ErrorCategory.LimitsExceeded,
        LdapResultCodes.Busy => ErrorCategory.ResourceUnavailable,
        LdapResultCodes.Unavailable => ErrorCategory.ResourceUnavailable,
        LdapResultCodes.ServerDown => ErrorCategory.ConnectionError,
        LdapResultCodes.Timeout => ErrorCategory.OperationTimeout,
        LdapResultCodes.OperationsError => ErrorCategory.InvalidOperation,
        LdapResultCodes.UnwillingToPerform => ErrorCategory.InvalidOperation,
        LdapResultCodes.InappropriateMatching => ErrorCategory.NotImplemented,
        _ => ErrorCategory.NotSpecified
    };

    private string DescribeLdapFailure(LdapException ex) =>
        DescribeResultCode(ex.ErrorCode, ex.Message);

    /// <summary>
    /// Append actionable guidance to raw LDAP errors, which are otherwise terse to the
    /// point of being unhelpful. Same intent as GraphServiceException's hint table.
    /// </summary>
    private string DescribeResultCode(int code, string message)
    {
        var hint = code switch
        {
            LdapResultCodes.InvalidCredentials =>
                " Check the username and password, and that the account is not locked or expired.",
            LdapResultCodes.InsufficientAccessRights =>
                " The bound account lacks read rights on this part of the directory.",
            LdapResultCodes.ServerDown =>
                $" Could not reach '{Server ?? Environment.GetEnvironmentVariable("USERDNSDOMAIN") ?? "the domain"}'. " +
                "Verify the host is reachable on the LDAP port and that -Server names a domain controller.",
            LdapResultCodes.SizeLimitExceeded =>
                " The server's MaxPageSize was exceeded. Lower -PageSize.",
            LdapResultCodes.TimeLimitExceeded or LdapResultCodes.Timeout =>
                " The search exceeded the server's MaxQueryDuration. Narrow -LdapFilter or -SearchBase, " +
                "or raise -SearchTimeout if the server policy allows it.",
            LdapResultCodes.AdminLimitExceeded =>
                " An administrative limit was hit (commonly MaxQueryDuration or MaxValRange). " +
                "Narrow the query or reduce -PageSize.",
            LdapResultCodes.NoSuchObject =>
                " Verify -SearchBase names an object that exists in this domain.",
            LdapResultCodes.InappropriateMatching =>
                " The server does not support the extended matching rule used by this query.",
            _ => string.Empty
        };

        return string.IsNullOrEmpty(hint) ? message : message.TrimEnd('.') + "." + hint;
    }

    #region Entry projection

    /// <summary>PSTypeName stamped on emitted entries, for Format.ps1xml dispatch.</summary>
    public const string EntryTypeName = "ADx.Entry";

    /// <summary>
    /// Project an <see cref="LdapEntry"/> onto a PSObject.
    /// <para>
    /// A PSObject rather than a fixed .NET type because the attribute set is decided by
    /// the caller's -Property list, so no static shape would fit.
    /// </para>
    /// <para>
    /// With <paramref name="raw"/> the values are passed through untouched (strings and
    /// byte arrays), which is the escape hatch when the conversions below get in the way.
    /// </para>
    /// </summary>
    protected static PSObject LdapEntryToPSObject(LdapEntry entry, bool raw)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, EntryTypeName);

        pso.Properties.Add(new PSNoteProperty("DistinguishedName", entry.DistinguishedName));

        if (!raw)
        {
            // The original collectors stripped the leading RDN and labelled the result
            // "DistinguishedName", which actually yields the parent container. Emit both,
            // named for what they are.
            pso.Properties.Add(new PSNoteProperty("ParentDn", LdapConvert.ParentDn(entry.DistinguishedName)));
        }

        foreach (var name in entry.Attributes.Keys)
        {
            if (string.Equals(name, "distinguishedName", StringComparison.OrdinalIgnoreCase))
                continue;

            // Emit under the BASE name, not the raw key. Past MaxValRange (default 1500) AD
            // does not truncate a multi-valued attribute -- it renames it, returning
            // "member;range=0-1499". Adding that verbatim produces a property literally called
            // "member;range=0-1499", so $group.member is null and the group reads as empty.
            // That is the exact failure LdapModels documents as the single most consequential
            // bug in naive AD collectors, and it is trivially reintroduced one layer up.
            var isRanged = LdapEntry.TryParseRangeOption(name, out var baseName, out _, out var high, out var isFinal);
            var propertyName = isRanged ? baseName! : name;

            pso.Properties.Add(new PSNoteProperty(
                propertyName, raw ? RawValue(entry, name) : ConvertValue(entry, name)));

            // Truncation must be visible rather than silent. A partial range means the caller
            // is holding fewer values than the directory has, and acting on that as if it were
            // the full set is how "the group looked empty" turns into a wrong access decision.
            if (isRanged && !isFinal)
            {
                pso.Properties.Add(new PSNoteProperty($"{baseName}Truncated", true));
                pso.Properties.Add(new PSNoteProperty($"{baseName}RangeHigh", high));
            }
        }

        if (!raw) AddDerivedProperties(pso, entry);

        return pso;
    }

    private static object? RawValue(LdapEntry entry, string name)
    {
        var values = entry.Attributes[name];
        return values.Count == 1 ? values[0] : values.ToArray();
    }

    private static object? ConvertValue(LdapEntry entry, string name)
    {
        // AdAttributeSchema is the single source of truth for "what kind of value is this
        // attribute" -- shared with the filter translator so a value that marshals one way
        // into a filter assertion can't decode a different way here.
        switch (AdAttributeSchema.SyntaxOf(name))
        {
            case AdAttributeSyntax.Sid:
            {
                // Every value, not just values[0]: sIDHistory is Sid-syntax AND multi-valued,
                // and GetBytes returns only the first, which silently dropped every migrated
                // SID but one -- the same defect the RSAT projector was fixed for, here on the
                // raw-search path. SDDL strings, matching Search-ADxObject's raw contract.
                var sids = entry.Attributes[name].OfType<byte[]>()
                    .Select(LdapConvert.SidToSddl)
                    .Where(s => s is not null)
                    .ToArray();
                return sids.Length switch { 0 => null, 1 => sids[0], _ => sids };
            }
            case AdAttributeSyntax.Guid:
                return LdapConvert.ObjectGuid(entry.GetBytes(name));
            case AdAttributeSyntax.GeneralizedTime:
                return LdapConvert.GeneralizedTime(entry.GetString(name));
            case AdAttributeSyntax.FileTime:
                return LdapConvert.FileTime(entry.GetString(name));
            case AdAttributeSyntax.Interval:
                return LdapConvert.Interval(entry.GetString(name));
            case AdAttributeSyntax.Integer:
                return entry.GetInt64(name);
            case AdAttributeSyntax.Binary:
            {
                // Binary-syntax attributes must stay byte[]: falling through to GetStrings
                // would UTF-8-decode the bytes into a mojibake string. -Raw already keeps
                // bytes; this makes the converted path agree.
                var bytes = entry.Attributes[name].OfType<byte[]>().ToArray();
                return bytes.Length switch { 0 => null, 1 => bytes[0], _ => bytes };
            }
            default:
                var strings = entry.GetStrings(name);
                return strings.Count == 1 ? strings[0] : strings.ToArray();
        }
    }

    /// <summary>
    /// Decode the bit-packed attributes into readable properties. userAccountControl and
    /// groupType are otherwise opaque integers -- and groupType arrives negative for every
    /// security group, which reads as nonsense without decoding.
    /// </summary>
    private static void AddDerivedProperties(PSObject pso, LdapEntry entry)
    {
        var uac = entry.GetInt32("userAccountControl");
        if (uac.HasValue)
        {
            var flags = LdapConvert.Uac(uac.Value);
            pso.Properties.Add(new PSNoteProperty("UacFlags", flags));
            pso.Properties.Add(new PSNoteProperty("Enabled", !flags.HasFlag(UacFlags.AccountDisabled)));
            pso.Properties.Add(new PSNoteProperty("PasswordNeverExpires", flags.HasFlag(UacFlags.DontExpirePassword)));
            pso.Properties.Add(new PSNoteProperty("TrustedForDelegation", flags.HasFlag(UacFlags.TrustedForDelegation)));
        }

        var groupType = entry.GetInt32("groupType");
        if (groupType.HasValue)
        {
            var info = LdapConvert.GroupType(groupType.Value);
            pso.Properties.Add(new PSNoteProperty("GroupScope", info.Scope.ToString()));
            pso.Properties.Add(new PSNoteProperty("GroupCategory", info.IsSecurity ? "Security" : "Distribution"));
        }
    }

    #endregion

    protected override void DisposeCore()
    {
        _client?.Dispose();
    }
}

/// <summary>Tab completion for -AuthType.</summary>
internal sealed class LdapAuthTypeCompleter : IArgumentCompleter
{
    private static readonly string[] Values = { "Negotiate", "Kerberos", "Basic", "Anonymous" };

    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        System.Management.Automation.Language.CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        foreach (var v in Values)
        {
            if (v.StartsWith(wordToComplete ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                yield return new CompletionResult(v, v, CompletionResultType.ParameterValue, v);
        }
    }
}
