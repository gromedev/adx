namespace ADx.Engine.Ldap;

/// <summary>
/// Completes range-limited multi-valued attributes.
/// <para>
/// Active Directory caps a single attribute read at <c>MaxValRange</c> (default 1500) values.
/// Past that it does not truncate -- it renames, returning <c>member;range=0-1499</c>, and the
/// remaining values are only reachable by asking again with <c>member;range=1500-*</c>, then
/// <c>member;range=3000-*</c>, and so on until the server answers with a <c>*</c> upper bound.
/// Each follow-up is a single-entry base-scope read: cheap, bounded, and proportional to the
/// attribute's size rather than the directory's.
/// </para>
/// </summary>
public static class LdapRangeRetriever
{
    /// <summary>
    /// Hard stop for the follow-up loop. 10,000 reads at 1,500 values each is a 15-million
    /// value attribute -- far past anything real; hitting this means the server is handing
    /// back ranges without making progress, and looping further would hang the pipeline.
    /// </summary>
    private const int MaxFollowUpReads = 10_000;

    /// <summary>Does the entry hold any attribute with an incomplete range?</summary>
    public static bool NeedsCompletion(LdapEntry entry)
    {
        foreach (var key in entry.Attributes.Keys)
        {
            if (LdapEntry.TryParseRangeOption(key, out _, out _, out _, out var isFinal) && !isFinal)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Return an entry in which every range-limited attribute carries its complete value set
    /// under its BASE name (the <c>;range=</c> keys are gone). Entries with no incomplete
    /// ranges are returned as-is, unchanged and unallocated.
    /// <para>
    /// <paramref name="warning"/> fires whenever the walk ends without the server confirming
    /// completeness -- the object vanished, the server stopped answering range requests, or a
    /// guard tripped. On those paths the returned values are whatever was fetched, and a
    /// caller acting on them as the full set would be making a decision on silently truncated
    /// data; the warning is what keeps "the group looked smaller than it is" diagnosable.
    /// </para>
    /// </summary>
    public static async Task<LdapEntry> CompleteAsync(
        ILdapSearchExecutor executor, LdapEntry entry, CancellationToken cancellationToken,
        Action<string>? warning = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(entry);

        if (!NeedsCompletion(entry)) return entry;

        // Base names that an incomplete ranged key will (re)write. A plain sibling of one of
        // these must never land in the result: dictionary enumeration order decides which
        // write happens last, and .NET randomises string hashing per process, so a plain
        // sibling clobbering the completed walk was a per-process coin flip, not a
        // theoretical concern. (LdapEntry's constructor already drops the EMPTY sibling AD
        // actually sends; this guard makes the merge order-independent for any shape.)
        var walkedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in entry.Attributes.Keys)
        {
            if (LdapEntry.TryParseRangeOption(key, out var b, out _, out _, out var f) && !f)
                walkedBases.Add(b!);
        }

        var rebuilt = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in entry.Attributes)
        {
            if (LdapEntry.TryParseRangeOption(key, out var baseName, out _, out var high, out var isFinal) && !isFinal)
            {
                rebuilt[baseName!] = await FetchRemainingAsync(
                    executor, entry.DistinguishedName, baseName!, values, high, cancellationToken, warning)
                    .ConfigureAwait(false);
            }
            else if (!walkedBases.Contains(key))
            {
                rebuilt[key] = values;
            }
        }

        return new LdapEntry(entry.DistinguishedName, rebuilt);
    }

    private static async Task<IReadOnlyList<object>> FetchRemainingAsync(
        ILdapSearchExecutor executor,
        string distinguishedName,
        string attributeName,
        IReadOnlyList<object> initialValues,
        int lastIndexSeen,
        CancellationToken cancellationToken,
        Action<string>? warning)
    {
        var all = new List<object>(initialValues);

        for (var reads = 0; reads < MaxFollowUpReads; reads++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = $"{attributeName};range={lastIndexSeen + 1}-*";
            var chunk = await executor.ReadEntryAsync(
                    distinguishedName, new[] { request }, cancellationToken)
                .ConfigureAwait(false);

            // The object vanished mid-walk (deleted, moved). There is nothing further to
            // fetch, but the caller is now holding a partial set and must be told so.
            if (chunk is null)
            {
                warning?.Invoke(
                    $"'{distinguishedName}' disappeared while '{attributeName}' was being range-retrieved; " +
                    $"returning the {all.Count} value(s) fetched before it vanished, which may not be the full set.");
                return all;
            }

            string? matchedKey = null;
            var chunkHigh = -1;
            var chunkFinal = false;
            foreach (var key in chunk.Attributes.Keys)
            {
                if (LdapEntry.TryParseRangeOption(key, out var chunkBase, out _, out var high, out var isFinal) &&
                    string.Equals(chunkBase, attributeName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = key;
                    chunkHigh = high;
                    chunkFinal = isFinal;
                    break;
                }
                // A server done with ranges may answer with the plain attribute name.
                if (string.Equals(key, attributeName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = key;
                    chunkFinal = true;
                    break;
                }
            }

            if (matchedKey is null)
            {
                warning?.Invoke(
                    $"The server answered a '{attributeName}' range request on '{distinguishedName}' without the " +
                    $"attribute; returning the {all.Count} value(s) fetched so far, which may not be the full set.");
                return all;
            }

            var values = chunk.Attributes[matchedKey];
            all.AddRange(values);

            if (chunkFinal) return all;

            // No progress (empty chunk with a non-final range) would loop forever; stop with
            // what we have rather than hang -- but say so.
            if (values.Count == 0 || chunkHigh <= lastIndexSeen)
            {
                warning?.Invoke(
                    $"The server stopped making progress on '{attributeName}' range retrieval for " +
                    $"'{distinguishedName}'; returning the {all.Count} value(s) fetched so far, which may not be the full set.");
                return all;
            }
            lastIndexSeen = chunkHigh;
        }

        warning?.Invoke(
            $"Gave up on '{attributeName}' for '{distinguishedName}' after {MaxFollowUpReads} follow-up reads; " +
            $"returning the {all.Count} value(s) fetched so far, which may not be the full set.");
        return all;
    }
}
