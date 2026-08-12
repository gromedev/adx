using System.Runtime.CompilerServices;

namespace ADx.Engine.Ldap;

/// <summary>Emitted after each page so callers can report progress or checkpoint.</summary>
public sealed record LdapPageCompletedInfo(int PageIndex, int EntriesInPage, long TotalEmitted, bool HasMore);

/// <summary>
/// Streams a paged LDAP search with a cursor -> page -> items -> cursor shape, driven by an
/// RFC 2696 byte cookie rather than a continuation URL.
/// <para>
/// The cookie is never surfaced for persistence. It is server-side session state, tied to
/// the LDAP connection that produced it, evicted under <c>MaxResultSetSize</c> pressure,
/// and dropped after <c>MaxConnIdleTime</c>. It cannot be saved and replayed on a fresh
/// connection; resume is built on partitioning instead.
/// </para>
/// </summary>
public sealed class LdapPageIterator
{
    // A server that returns an empty page while still handing back a cookie would otherwise
    // spin forever. Matches PageIterator's guard.
    private const int MaxConsecutiveEmptyPages = 3;

    private readonly ILdapSearchExecutor _executor;

    public LdapPageIterator(ILdapSearchExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Stream every entry matching <paramref name="spec"/>.
    /// </summary>
    /// <param name="maxItems">Upper bound on entries emitted; 0 means unlimited.</param>
    /// <param name="onPageComplete">Invoked after each page is fully emitted.</param>
    /// <param name="skipFirst">
    /// Entries to discard before emitting. Used when resuming inside a partition whose
    /// first N entries were already written.
    /// </param>
    public async IAsyncEnumerable<LdapEntry> StreamAsync(
        LdapSearchSpec spec,
        long maxItems = 0,
        Action<LdapPageCompletedInfo>? onPageComplete = null,
        long skipFirst = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        byte[]? cookie = null;
        var pageIndex = 0;
        long emitted = 0;
        long skipped = 0;
        var consecutiveEmptyPages = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _executor.SearchPageAsync(spec, cookie, cancellationToken).ConfigureAwait(false);
            pageIndex++;

            if (page.Entries.Count == 0)
            {
                if (!page.HasMore) yield break;
                if (++consecutiveEmptyPages >= MaxConsecutiveEmptyPages) yield break;
                cookie = page.Cookie;
                continue;
            }

            consecutiveEmptyPages = 0;

            foreach (var entry in page.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (skipped < skipFirst)
                {
                    skipped++;
                    continue;
                }

                yield return entry;
                emitted++;

                if (maxItems > 0 && emitted >= maxItems)
                {
                    onPageComplete?.Invoke(new LdapPageCompletedInfo(
                        pageIndex, page.Entries.Count, emitted, page.HasMore));
                    yield break;
                }
            }

            onPageComplete?.Invoke(new LdapPageCompletedInfo(
                pageIndex, page.Entries.Count, emitted, page.HasMore));

            if (!page.HasMore) yield break;
            cookie = page.Cookie;
        }
    }
}
