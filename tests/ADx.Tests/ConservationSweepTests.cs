using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// Tier-3 of the data-integrity programme: CONSERVATION properties over the engine's two
/// enumeration mechanisms. The module's defining failure class is silently dropped results,
/// and the two seams where a drop can hide are page boundaries (the cookie walk) and range
/// boundaries (the MaxValRange follow-up walk). These sweeps generate hundreds of seeded
/// page/range decompositions and assert exact conservation on every one: entries in ==
/// entries out, no duplicates, order preserved, and every truncation accounted for. A fixed
/// seed makes every run identical -- a failure names the case's parameters, reproducibly.
/// </summary>
public class ConservationSweepTests
{
    private static readonly LdapSearchSpec Spec = new(
        "DC=corp,DC=contoso,DC=com", "(objectClass=user)", new[] { "sAMAccountName" });

    private static async Task<List<LdapEntry>> DrainAsync(
        FakeLdapExecutor fake, long maxItems = 0, long skipFirst = 0)
    {
        var result = new List<LdapEntry>();
        await foreach (var e in new LdapPageIterator(fake)
                           .StreamAsync(Spec, maxItems, skipFirst: skipFirst))
            result.Add(e);
        return result;
    }

    /// <summary>
    /// A random page decomposition of <paramref name="total"/> entries: chunk sizes 1..1000,
    /// cookies chaining every page but the last. Entry DNs carry their global index, so
    /// order and uniqueness are checkable on the way out.
    /// </summary>
    private static List<LdapPage> Decompose(System.Random rng, int total)
    {
        var pages = new List<LdapPage>();
        var emitted = 0;
        byte cookieByte = 1;
        while (emitted < total)
        {
            var size = Math.Min(rng.Next(1, 1001), total - emitted);
            var last = emitted + size >= total;
            pages.Add(FakeLdapExecutor.Page(size, emitted, last ? null : new[] { cookieByte++ }));
            emitted += size;
        }
        if (pages.Count == 0) pages.Add(new LdapPage(Array.Empty<LdapEntry>(), null));
        return pages;
    }

    private static void AssertExactSequence(List<LdapEntry> entries, int expectedCount, int startIndex = 0)
    {
        Assert.Equal(expectedCount, entries.Count);
        for (var i = 0; i < entries.Count; i++)
            Assert.Equal($"CN=user{startIndex + i},DC=corp,DC=contoso,DC=com", entries[i].DistinguishedName);
        // The per-index DN assertion above already proves uniqueness and order; make the
        // set property explicit anyway so a future refactor of Page() cannot weaken it.
        Assert.Equal(entries.Count, entries.Select(e => e.DistinguishedName).Distinct().Count());
    }

    [Fact]
    public async Task PagingConservation_SweepOverRandomDecompositions()
    {
        // Fixed seed: the 200 cases are the same forever. Totals hug the page-size cliff
        // edges deliberately -- 0, 1, 999..1001, 1999..2001 -- plus uniform coverage.
        var rng = new System.Random(20260813);
        var pinnedTotals = new[] { 0, 1, 2, 999, 1000, 1001, 1999, 2000, 2001, 2500 };

        for (var caseIndex = 0; caseIndex < 200; caseIndex++)
        {
            var total = caseIndex < pinnedTotals.Length
                ? pinnedTotals[caseIndex]
                : rng.Next(0, 2501);

            var fake = new FakeLdapExecutor(Decompose(rng, total));
            var entries = await DrainAsync(fake);

            AssertExactSequence(entries, total);
        }
    }

    [Fact]
    public async Task PagingConservation_MaxItemsAndSkipAreExact()
    {
        // maxItems is the -ResultSetSize seam and skipFirst the checkpoint-resume seam:
        // both must slice EXACTLY -- expected = clamp(total - skip, 0, max), starting at
        // index skip -- for every decomposition. An off-by-one here is a silently dropped
        // (or duplicated) row at a resume boundary.
        var rng = new System.Random(4040404);

        for (var caseIndex = 0; caseIndex < 120; caseIndex++)
        {
            var total = rng.Next(0, 2001);
            var max = rng.Next(0, total + 50);      // 0 = unlimited
            var skip = rng.Next(0, total + 5);

            var fake = new FakeLdapExecutor(Decompose(rng, total));
            var entries = await DrainAsync(fake, maxItems: max, skipFirst: skip);

            var afterSkip = Math.Max(0, total - skip);
            var expected = max == 0 ? afterSkip : Math.Min(max, afterSkip);
            AssertExactSequence(entries, expected, startIndex: skip);
        }
    }

    [Fact]
    public async Task RangeRetrievalConservation_SweepOverRandomBlockSplits()
    {
        // K member values split across arbitrary server-side range blocks must come back as
        // exactly K, in order, however the server chunks the walk. The fake answers each
        // follow-up "member;range=N-*" with a random-width block, final block starred.
        var rng = new System.Random(31337);

        for (var caseIndex = 0; caseIndex < 60; caseIndex++)
        {
            var totalValues = rng.Next(2, 5000);
            var firstBlock = Math.Min(rng.Next(1, 1500), totalValues - 1);

            var initial = RangedEntry("CN=Big,DC=corp", 0, firstBlock - 1, isFinal: false);

            var blockRng = new System.Random(caseIndex * 7919 + 17);
            var fake = new FakeLdapExecutor(
                Array.Empty<LdapPage>(),
                readEntry: (dn, attrs) =>
                {
                    var requested = Assert.Single(attrs);
                    var from = int.Parse(requested["member;range=".Length..].Split('-')[0]);
                    var width = blockRng.Next(1, 1500);
                    var toInclusive = Math.Min(from + width - 1, totalValues - 1);
                    var final = toInclusive >= totalValues - 1;
                    return RangedEntry(dn, from, toInclusive, final);
                });

            var completed = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

            var members = completed.GetStrings("member");
            Assert.Equal(totalValues, members.Count);
            for (var i = 0; i < totalValues; i++)
                Assert.Equal($"CN=m{i},DC=corp", members[i]);
        }
    }

    private static LdapEntry RangedEntry(string dn, int from, int toInclusive, bool isFinal)
    {
        var values = new List<object>();
        for (var i = from; i <= toInclusive; i++) values.Add($"CN=m{i},DC=corp");

        var key = isFinal ? $"member;range={from}-*" : $"member;range={from}-{toInclusive}";
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = values
        };
        return new LdapEntry(dn, dict);
    }
}
