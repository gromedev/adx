using System.DirectoryServices.Protocols;

namespace ADx.Engine.Ldap;

/// <summary>
/// Thrown when the platform's LDAP client library is unavailable. On Linux/macOS
/// <c>S.DS.Protocols</c> P/Invokes OpenLDAP, which slim container images typically omit.
/// Distinguished from an ordinary connection failure so callers can give real guidance
/// instead of surfacing a <c>DllNotFoundException</c> stack trace.
/// </summary>
public sealed class LdapRuntimeMissingException : Exception
{
    public LdapRuntimeMissingException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// The one class that talks to <c>System.DirectoryServices.Protocols</c>. Everything else
/// in <see cref="ADx.Engine.Ldap"/> works against <see cref="ILdapSearchExecutor"/> and is
/// testable without a directory.
/// </summary>
public sealed class ADxLdapClient : ILdapSearchExecutor
{
    // Attributes AD returns as raw bytes. S.DS.Protocols surfaces values as strings when
    // they decode as UTF-8, which silently corrupts binary data, so these are forced.
    private static readonly HashSet<string> BinaryAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "objectSid", "objectGUID", "sIDHistory", "tokenGroups", "thumbnailPhoto",
        "jpegPhoto", "userCertificate", "msDS-KeyCredentialLink", "userParameters",
        "logonHours", "schemaIDGUID", "attributeSecurityGUID", "nTSecurityDescriptor",
        "invocationId"
    };

    private readonly LdapConnection _connection;
    private readonly LdapClientOptions _options;
    private readonly TimeSpan _searchTimeout;
    private int _disposed;

    public string? ConnectedServer { get; }
    public LdapRootDse RootDse { get; private set; } = null!;

    private ADxLdapClient(LdapConnection connection, LdapClientOptions options, string server)
    {
        _connection = connection;
        _options = options;
        ConnectedServer = server;
        _searchTimeout = TimeSpan.FromSeconds(options.SearchTimeoutSeconds);
    }

    /// <summary>
    /// Connect, bind (with retry), and read RootDSE.
    /// </summary>
    /// <exception cref="LdapRuntimeMissingException">OpenLDAP is not installed.</exception>
    /// <exception cref="InvalidOperationException">No server given and none discoverable.</exception>
    /// <exception cref="LdapException">Connection or bind failed after all retries.</exception>
    public static async Task<ADxLdapClient> ConnectAsync(
        LdapClientOptions options,
        Action<string>? verbose = null,
        Action<string>? warning = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var server = ResolveServer(options.Server);
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new InvalidOperationException(
                "No directory server specified and none could be discovered. " +
                "Pass -Server <dc.contoso.com>; automatic discovery requires a domain-joined host " +
                "with USERDNSDOMAIN set.");
        }

        verbose?.Invoke($"Connecting to LDAP server '{server}:{options.Port}' (SSL: {options.UseSsl}).");

        LdapConnection connection;
        try
        {
            var identifier = new LdapDirectoryIdentifier(server, options.Port);
            connection = new LdapConnection(identifier)
            {
                AuthType = MapAuthType(options.AuthMode),
                Timeout = TimeSpan.FromSeconds(options.SearchTimeoutSeconds)
            };

            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = options.ChaseReferrals
                ? ReferralChasingOptions.All
                : ReferralChasingOptions.None;

            if (options.UseSsl)
                connection.SessionOptions.SecureSocketLayer = true;

            // Signing/sealing is a Windows-only session option. Guarded rather than
            // try/caught so CA1416 can verify the platform check statically.
            if (options.RequireSigning && !options.UseSsl && OperatingSystem.IsWindows())
            {
                try
                {
                    connection.SessionOptions.Signing = true;
                    connection.SessionOptions.Sealing = true;
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or DirectoryOperationException)
                {
                    warning?.Invoke($"LDAP signing/sealing unavailable; continuing unsigned. ({ex.Message})");
                }
            }

            WarnOnUnprotectedTransport(options, warning);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            throw new LdapRuntimeMissingException(
                "The OpenLDAP client library could not be loaded. Install it and retry " +
                "(Debian/Ubuntu: 'apt install libldap-2.5-0'; RHEL/Fedora: 'dnf install openldap').",
                ex);
        }

        var client = new ADxLdapClient(connection, options, server);

        try
        {
            await client.BindWithRetryAsync(verbose, warning, cancellationToken).ConfigureAwait(false);
            client.RootDse = await client.ReadRootDseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        verbose?.Invoke(
            $"Bound to '{client.RootDse.DnsHostName ?? server}'. " +
            $"defaultNamingContext: {client.RootDse.DefaultNamingContext ?? "(none)"}.");

        return client;
    }

    /// <summary>
    /// Server name resolution. Prefers the caller's value, then the full DNS domain.
    /// </summary>
    private static string? ResolveServer(string? explicitServer)
    {
        if (!string.IsNullOrWhiteSpace(explicitServer)) return explicitServer.Trim();

        // The FULL value, not the first label: splitting "corp.contoso.com" on '.' and
        // taking [0] yields "corp", which resolves only when a DNS suffix search list
        // happens to complete it.
        var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        return string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
    }

    private static AuthType MapAuthType(LdapAuthMode mode) => mode switch
    {
        LdapAuthMode.Kerberos => AuthType.Kerberos,
        LdapAuthMode.Basic => AuthType.Basic,
        LdapAuthMode.Anonymous => AuthType.Anonymous,
        _ => AuthType.Negotiate
    };

    /// <summary>
    /// Warn when credentials will cross the wire without confidentiality.
    /// <para>
    /// Two distinct cases, neither of which announced itself before. A simple BIND
    /// (<c>AuthType.Basic</c>) without SSL sends the password in cleartext on port 389 --
    /// recoverable from a packet capture. Separately, signing/sealing is a Windows-only
    /// session option, so on Linux and macOS a Negotiate/Kerberos bind runs with no
    /// integrity protection at all and the Windows-path "continuing unsigned" warning above
    /// never fires. The operator may well have accepted these trade-offs; the point is that
    /// it should be their decision, made visibly, not a silent default.
    /// </para>
    /// </summary>
    private static void WarnOnUnprotectedTransport(LdapClientOptions options, Action<string>? warning)
    {
        if (options.UseSsl || warning is null) return;

        if (options.AuthMode == LdapAuthMode.Basic)
        {
            warning(
                "-AuthType Basic without -UseSsl sends the password in CLEARTEXT over the network. " +
                "Use -UseSsl (LDAPS, port 636) or -AuthType Negotiate/Kerberos instead.");
            return;
        }

        if (options.RequireSigning && !OperatingSystem.IsWindows() && options.AuthMode != LdapAuthMode.Anonymous)
        {
            warning(
                "LDAP signing/sealing is a Windows-only session option, so this connection is unsigned and " +
                "unencrypted. Use -UseSsl (LDAPS, port 636) if the traffic crosses an untrusted network.");
        }
    }

    private async Task BindWithRetryAsync(
        Action<string>? verbose, Action<string>? warning, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_options.Credential is not null)
                    _connection.Bind(_options.Credential);
                else
                    _connection.Bind();

                if (attempt > 0) verbose?.Invoke($"Bind succeeded on attempt {attempt + 1}.");
                return;
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
            {
                throw new LdapRuntimeMissingException(
                    "The OpenLDAP client library could not be loaded. Install it and retry " +
                    "(Debian/Ubuntu: 'apt install libldap-2.5-0'; RHEL/Fedora: 'dnf install openldap').",
                    ex);
            }
            catch (LdapException ex) when (IsUnsupportedSaslBind(ex))
            {
                // Verified against a real DC from macOS: a Negotiate or Kerberos bind carrying
                // EXPLICIT credentials fails here with the bare message "The feature is not
                // supported." libldap on Linux/macOS cannot do SASL/GSSAPI with a username and
                // password handed to it; Windows can, because it brokers through SSPI.
                //
                // Negotiate is the default AuthType, so without this the first thing a
                // non-Windows caller tries -- the documented -Credential path -- fails with a
                // message that names neither the cause nor the way out. That undercuts the
                // cross-platform story specifically, which is the reason this rewrite exists.
                throw new LdapException(
                    ex.ErrorCode,
                    $"-AuthType {_options.AuthMode} with -Credential is not supported by the LDAP client " +
                    "library on Linux and macOS (only Windows can broker Negotiate/Kerberos through SSPI). " +
                    "Either use '-AuthType Basic -UseSsl' (a simple bind, encrypted by LDAPS -- never Basic " +
                    "without -UseSsl, which sends the password in cleartext), or obtain a Kerberos ticket " +
                    "with 'kinit user@REALM' and omit -Credential so the existing ticket is used.",
                    ex);
            }
            catch (LdapException ex) when (attempt < _options.MaxRetryAttempts && IsRetryable(ex))
            {
                attempt++;
                warning?.Invoke(
                    $"LDAP bind failed (attempt {attempt} of {_options.MaxRetryAttempts}): {ex.Message} " +
                    $"Retrying in {_options.RetryDelaySeconds}s.");
                await Task.Delay(TimeSpan.FromSeconds(_options.RetryDelaySeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The non-Windows "SASL bind with explicit credentials" failure. Matched on message text
    /// because the platform surfaces it as a generic error code rather than a distinct one --
    /// narrowed by the two conditions that must also hold (a SASL auth mode, and credentials
    /// actually supplied) so an unrelated error carrying similar wording cannot be
    /// misreported as this.
    /// </summary>
    private bool IsUnsupportedSaslBind(LdapException ex) =>
        !OperatingSystem.IsWindows() &&
        _options.Credential is not null &&
        _options.AuthMode is LdapAuthMode.Negotiate or LdapAuthMode.Kerberos &&
        ex.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Transient conditions worth retrying. Credential and access-rights failures are
    /// deliberately excluded: they fail identically every time, so retrying only delays
    /// the error the operator needs to see.
    /// </summary>
    private static bool IsRetryable(LdapException ex) => ex.ErrorCode switch
    {
        LdapResultCodes.Busy => true,
        LdapResultCodes.Unavailable => true,
        LdapResultCodes.ServerDown => true,
        LdapResultCodes.Timeout => true,
        _ => false
    };

    private async Task<LdapRootDse> ReadRootDseAsync(CancellationToken cancellationToken)
    {
        string[] attrs =
        {
            "defaultNamingContext", "configurationNamingContext", "schemaNamingContext",
            "rootDomainNamingContext", "dnsHostName", "serverName",
            "highestCommittedUSN", "domainControllerFunctionality", "supportedControl"
        };

        var request = new SearchRequest(string.Empty, "(objectClass=*)", SearchScope.Base, attrs);
        var response = (SearchResponse)await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Entries.Count == 0)
        {
            return new LdapRootDse(null, null, null, null, null, null, null, null,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var entry = Project(response.Entries[0]);
        var controls = new HashSet<string>(entry.GetStrings("supportedControl"), StringComparer.OrdinalIgnoreCase);

        return new LdapRootDse(
            entry.GetString("defaultNamingContext"),
            entry.GetString("configurationNamingContext"),
            entry.GetString("schemaNamingContext"),
            entry.GetString("rootDomainNamingContext"),
            entry.GetString("dnsHostName"),
            entry.GetString("serverName"),
            entry.GetInt64("highestCommittedUSN"),
            entry.GetInt32("domainControllerFunctionality"),
            controls);
    }

    public async Task<LdapPage> SearchPageAsync(
        LdapSearchSpec spec, byte[]? cookie, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(_disposed == 1, this);

        var request = new SearchRequest(
            spec.SearchBase,
            spec.Filter,
            MapScope(spec.Scope),
            spec.Attributes?.ToArray() ?? Array.Empty<string>());

        if (spec.SizeLimit > 0) request.SizeLimit = spec.SizeLimit;

        var paging = new PageResultRequestControl(spec.PageSize);
        if (cookie is { Length: > 0 }) paging.Cookie = cookie;
        request.Controls.Add(paging);

        // Keeps a search from wandering into other domains when referrals are chased.
        if (!_options.ChaseReferrals && RootDse.SupportsDomainScope)
            request.Controls.Add(new DomainScopeControl());

        var response = (SearchResponse)await SendAsync(request, cancellationToken).ConfigureAwait(false);

        var entries = new List<LdapEntry>(response.Entries.Count);
        foreach (SearchResultEntry entry in response.Entries)
            entries.Add(Project(entry));

        byte[]? nextCookie = null;
        foreach (DirectoryControl control in response.Controls)
        {
            if (control is PageResultResponseControl page)
            {
                nextCookie = page.Cookie;
                break;
            }
        }

        return new LdapPage(entries, nextCookie);
    }

    public async Task<LdapEntry?> ReadEntryAsync(
        string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        ObjectDisposedException.ThrowIf(_disposed == 1, this);

        var request = new SearchRequest(
            distinguishedName,
            "(objectClass=*)",
            SearchScope.Base,
            attributes?.ToArray() ?? Array.Empty<string>());

        try
        {
            var response = (SearchResponse)await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.Entries.Count == 0 ? null : Project(response.Entries[0]);
        }
        catch (DirectoryOperationException ex)
            when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
    }

    /// <summary>
    /// Bridge <c>S.DS.Protocols</c>' APM pair onto a cancellable Task.
    /// <para>
    /// The synchronous <c>SendRequest</c> blocks uninterruptibly until
    /// <c>LdapConnection.Timeout</c> elapses, so wrapping it in <c>Task.Run</c> and
    /// cancelling would free the pipeline thread while leaving a worker stuck for up to the
    /// full timeout -- which is what breaks Ctrl-C. <c>BeginSendRequest</c> plus
    /// <c>Abort</c> is the designed cancellation path, and <c>Abort</c> maps to
    /// <c>ldap_abandon</c> on Linux/macOS as well as Windows.
    /// </para>
    /// <para>
    /// Note this buys responsiveness, not throughput: page N+1 needs page N's cookie, so a
    /// paged search is inherently sequential.
    /// </para>
    /// </summary>
    private Task<DirectoryResponse> SendAsync(DirectoryRequest request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<DirectoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            tcs.TrySetCanceled(cancellationToken);
            return tcs.Task;
        }

        CancellationTokenRegistration registration = default;

        try
        {
            var asyncResult = _connection.BeginSendRequest(
                request,
                _searchTimeout,
                PartialResultProcessing.NoPartialResultSupport,
                iar =>
                {
                    try { tcs.TrySetResult(_connection.EndSendRequest(iar)); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                },
                null);

            registration = cancellationToken.Register(() =>
            {
                try { _connection.Abort(asyncResult); }
                catch { /* best effort: the request may already have completed */ }
                tcs.TrySetCanceled(cancellationToken);
            });
        }
        catch (Exception ex)
        {
            registration.Dispose();
            tcs.TrySetException(ex);
            return tcs.Task;
        }

        return tcs.Task.ContinueWith(
            t => { registration.Dispose(); return t; },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }

    private static SearchScope MapScope(LdapScope scope) => scope switch
    {
        LdapScope.Base => SearchScope.Base,
        LdapScope.OneLevel => SearchScope.OneLevel,
        _ => SearchScope.Subtree
    };

    /// <summary>
    /// Project a <c>SearchResultEntry</c> onto <see cref="LdapEntry"/>. This is the only
    /// place S.DS.Protocols types cross into ADx's own model.
    /// </summary>
    private static LdapEntry Project(SearchResultEntry entry)
    {
        var attributes = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in entry.Attributes.AttributeNames)
        {
            var attribute = entry.Attributes[name];
            if (attribute is null) continue;

            // The base name matters for the binary check: a range-limited attribute
            // arrives as "member;range=0-1499", which would not match a plain lookup.
            var baseName = LdapEntry.TryParseRangeOption(name, out var parsed, out _, out _, out _)
                ? parsed
                : name;

            List<object> values;
            if (BinaryAttributes.Contains(baseName))
            {
                // Force bytes: left to itself S.DS.Protocols hands back a string whenever
                // the value happens to decode as UTF-8, corrupting SIDs and GUIDs.
                var raw = attribute.GetValues(typeof(byte[]));
                values = new List<object>(raw.Length);
                foreach (var v in raw) values.Add(v);
            }
            else
            {
                values = new List<object>(attribute.Count);
                for (var i = 0; i < attribute.Count; i++) values.Add(attribute[i]);
            }

            attributes[name] = values;
        }

        return new LdapEntry(entry.DistinguishedName ?? string.Empty, attributes);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        try { _connection.Dispose(); } catch { /* best-effort teardown */ }
    }
}
