namespace ADx.Engine.Ldap;

/// <summary>
/// The seam between ADx and <c>System.DirectoryServices.Protocols</c>.
/// <para>
/// Everything above this interface -- paging, range completion, membership resolution,
/// checkpointing -- works on ADx's own <see cref="LdapEntry"/>/<see cref="LdapPage"/> types
/// and can therefore be tested against a fake with no directory server present. Only
/// <see cref="ADxLdapClient"/> implements it, and only that class references
/// <c>S.DS.Protocols</c>.
/// </para>
/// <para>
/// This abstraction is not optional. <c>SearchResultEntry</c>, <c>SearchResponse</c>,
/// <c>SearchResultEntryCollection</c> and <c>PageResultResponseControl</c> all expose
/// internal-only constructors and have no accessible base constructor, so they can neither
/// be constructed nor subclassed from a test assembly.
/// </para>
/// </summary>
public interface ILdapSearchExecutor : IDisposable
{
    /// <summary>The server actually connected to, for diagnostics and checkpoint identity.</summary>
    string? ConnectedServer { get; }

    /// <summary>RootDSE, read during connect.</summary>
    LdapRootDse RootDse { get; }

    /// <summary>
    /// Execute one page. Pass null as <paramref name="cookie"/> for the first page, then the
    /// cookie from the previous <see cref="LdapPage"/>.
    /// </summary>
    Task<LdapPage> SearchPageAsync(LdapSearchSpec spec, byte[]? cookie, CancellationToken cancellationToken);

    /// <summary>
    /// Base-scope read of a single entry. Used for range retrieval continuation
    /// (<c>member;range=1500-*</c>) and for resolving individual DNs.
    /// Returns null when the object does not exist.
    /// </summary>
    Task<LdapEntry?> ReadEntryAsync(
        string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken);
}
