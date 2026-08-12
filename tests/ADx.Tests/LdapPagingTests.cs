using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// A scripted <see cref="ILdapSearchExecutor"/>.
/// <para>
/// This fake is why <see cref="ILdapSearchExecutor"/> exists at all: S.DS.Protocols'
/// <c>SearchResultEntry</c>, <c>SearchResponse</c> and <c>PageResultResponseControl</c>
/// expose only internal constructors and have no accessible base constructor, so they can
/// be neither constructed nor subclassed here. Projecting to ADx's own LdapEntry/LdapPage
/// at the client boundary is what makes the paging loop testable without a domain.
/// </para>
/// </summary>
internal sealed class FakeLdapExecutor : ILdapSearchExecutor
{
    private readonly Queue<LdapPage> _pages;
    private readonly Func<string, IReadOnlyList<string>, LdapEntry?>? _readEntry;

    public int SearchCallCount { get; private set; }
    public int ReadEntryCallCount { get; private set; }
    public List<byte[]?> CookiesSeen { get; } = new();
    public List<IReadOnlyList<string>> AttributeRequestsSeen { get; } = new();
    public string? ConnectedServer => "fake.contoso.com";
    public LdapRootDse RootDse { get; }

    public FakeLdapExecutor(
        IEnumerable<LdapPage> pages,
        LdapRootDse? rootDse = null,
        Func<string, IReadOnlyList<string>, LdapEntry?>? readEntry = null)
    {
        _pages = new Queue<LdapPage>(pages);
        _readEntry = readEntry;
        RootDse = rootDse ?? new LdapRootDse(
            "DC=corp,DC=contoso,DC=com", null, null, null, "dc1.corp.contoso.com", "DC1",
            12345, 7, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LdapRootDse.OidPagedResults });
    }

    public Task<LdapPage> SearchPageAsync(LdapSearchSpec spec, byte[]? cookie, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchCallCount++;
        CookiesSeen.Add(cookie);
        return Task.FromResult(_pages.Count > 0 ? _pages.Dequeue() : new LdapPage(Array.Empty<LdapEntry>(), null));
    }

    public Task<LdapEntry?> ReadEntryAsync(
        string distinguishedName, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadEntryCallCount++;
        AttributeRequestsSeen.Add(attributes);
        return Task.FromResult(_readEntry?.Invoke(distinguishedName, attributes));
    }

    public void Dispose() { }

    public static LdapEntry Entry(string dn, params (string Name, object Value)[] attributes)
    {
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in attributes) dict[name] = new List<object> { value };
        return new LdapEntry(dn, dict);
    }

    public static LdapPage Page(int count, int startAt, byte[]? cookie)
    {
        var entries = new List<LdapEntry>(count);
        for (var i = 0; i < count; i++)
            entries.Add(Entry($"CN=user{startAt + i},DC=corp,DC=contoso,DC=com",
                ("sAMAccountName", $"user{startAt + i}")));
        return new LdapPage(entries, cookie);
    }
}

public class LdapPagingTests
{
    private static readonly LdapSearchSpec Spec = new(
        "DC=corp,DC=contoso,DC=com", "(objectClass=user)", new[] { "sAMAccountName" });

    private static async Task<List<LdapEntry>> DrainAsync(
        LdapPageIterator iterator, LdapSearchSpec spec, long maxItems = 0, long skipFirst = 0,
        CancellationToken ct = default)
    {
        var result = new List<LdapEntry>();
        await foreach (var e in iterator.StreamAsync(spec, maxItems, skipFirst: skipFirst, cancellationToken: ct))
            result.Add(e);
        return result;
    }

    [Fact]
    public async Task FollowsCookieAcrossPagesAndStopsWhenCookieIsEmpty()
    {
        var fake = new FakeLdapExecutor(new[]
        {
            FakeLdapExecutor.Page(3, 0, new byte[] { 1 }),
            FakeLdapExecutor.Page(3, 3, new byte[] { 2 }),
            FakeLdapExecutor.Page(2, 6, null)   // no cookie => last page
        });

        var entries = await DrainAsync(new LdapPageIterator(fake), Spec);

        Assert.Equal(8, entries.Count);
        Assert.Equal(3, fake.SearchCallCount);
        // First request carries no cookie; each subsequent one carries the previous page's.
        Assert.Null(fake.CookiesSeen[0]);
        Assert.Equal(new byte[] { 1 }, fake.CookiesSeen[1]);
        Assert.Equal(new byte[] { 2 }, fake.CookiesSeen[2]);
    }

    [Fact]
    public async Task TopStopsEarlyWithoutFetchingFurtherPages()
    {
        var fake = new FakeLdapExecutor(new[]
        {
            FakeLdapExecutor.Page(5, 0, new byte[] { 1 }),
            FakeLdapExecutor.Page(5, 5, new byte[] { 2 })
        });

        var entries = await DrainAsync(new LdapPageIterator(fake), Spec, maxItems: 3);

        Assert.Equal(3, entries.Count);
        Assert.Equal(1, fake.SearchCallCount);
    }

    [Fact]
    public async Task MaxItemsZeroMeansUnlimited()
    {
        var fake = new FakeLdapExecutor(new[]
        {
            FakeLdapExecutor.Page(4, 0, new byte[] { 1 }),
            FakeLdapExecutor.Page(4, 4, null)
        });

        Assert.Equal(8, (await DrainAsync(new LdapPageIterator(fake), Spec, maxItems: 0)).Count);
    }

    [Fact]
    public async Task SkipFirstDiscardsLeadingEntriesForPartitionResume()
    {
        var fake = new FakeLdapExecutor(new[] { FakeLdapExecutor.Page(5, 0, null) });

        var entries = await DrainAsync(new LdapPageIterator(fake), Spec, skipFirst: 2);

        Assert.Equal(3, entries.Count);
        Assert.Equal("CN=user2,DC=corp,DC=contoso,DC=com", entries[0].DistinguishedName);
    }

    [Fact]
    public async Task EmptyFirstPageWithNoCookieYieldsNothing()
    {
        var fake = new FakeLdapExecutor(new[] { new LdapPage(Array.Empty<LdapEntry>(), null) });

        Assert.Empty(await DrainAsync(new LdapPageIterator(fake), Spec));
    }

    [Fact]
    public async Task BailsOutRatherThanSpinningOnEndlessEmptyPages()
    {
        // A server that keeps returning a cookie with no entries would otherwise loop forever.
        var pages = Enumerable.Range(0, 50)
            .Select(_ => new LdapPage(Array.Empty<LdapEntry>(), new byte[] { 9 }));

        var fake = new FakeLdapExecutor(pages);
        var entries = await DrainAsync(new LdapPageIterator(fake), Spec);

        Assert.Empty(entries);
        Assert.True(fake.SearchCallCount <= 3, $"Expected the empty-page guard to trip, saw {fake.SearchCallCount} calls.");
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var fake = new FakeLdapExecutor(new[]
        {
            FakeLdapExecutor.Page(5, 0, new byte[] { 1 }),
            FakeLdapExecutor.Page(5, 5, new byte[] { 2 })
        });

        using var cts = new CancellationTokenSource();
        var iterator = new LdapPageIterator(fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var seen = 0;
            await foreach (var _ in iterator.StreamAsync(Spec, cancellationToken: cts.Token))
            {
                if (++seen == 2) cts.Cancel();
            }
        });
    }

    [Fact]
    public async Task PageCompleteCallbackReportsProgress()
    {
        var fake = new FakeLdapExecutor(new[]
        {
            FakeLdapExecutor.Page(3, 0, new byte[] { 1 }),
            FakeLdapExecutor.Page(2, 3, null)
        });

        var reported = new List<LdapPageCompletedInfo>();
        await foreach (var _ in new LdapPageIterator(fake)
                           .StreamAsync(Spec, onPageComplete: reported.Add))
        {
        }

        Assert.Equal(2, reported.Count);
        Assert.Equal(3, reported[0].TotalEmitted);
        Assert.True(reported[0].HasMore);
        Assert.Equal(5, reported[1].TotalEmitted);
        Assert.False(reported[1].HasMore);
    }
}

public class LdapRangeRetrievalTests
{
    [Fact]
    public void UnrangedAttributeIsReturnedComplete()
    {
        var entry = FakeLdapExecutor.Entry("CN=Small,DC=corp", ("member", "CN=a,DC=corp"));

        Assert.True(entry.TryGetRanged("member", out var values, out _, out _, out var isFinal));
        Assert.Single(values);
        Assert.True(isFinal);
    }

    [Fact]
    public void RangedAttributeIsFoundUnderItsRenamedKey()
    {
        // The bug this guards: past MaxValRange (1500) AD renames the attribute to
        // "member;range=0-1499", so a plain lookup of "member" finds nothing and the
        // caller concludes the group is empty. Every group over 1500 members reported 0.
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)
        {
            ["member;range=0-1499"] = new List<object> { "CN=a,DC=corp", "CN=b,DC=corp" }
        };
        var entry = new LdapEntry("CN=Big,DC=corp", dict);

        Assert.Null(entry.GetString("member"));            // the naive lookup still sees nothing
        Assert.True(entry.TryGetRanged("member", out var values, out var low, out var high, out var isFinal));
        Assert.Equal(2, values.Count);
        Assert.Equal(0, low);
        Assert.Equal(1499, high);
        Assert.False(isFinal);                              // more reads required
    }

    [Fact]
    public void FinalRangeIsMarkedComplete()
    {
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)
        {
            ["member;range=1500-*"] = new List<object> { "CN=z,DC=corp" }
        };
        var entry = new LdapEntry("CN=Big,DC=corp", dict);

        Assert.True(entry.TryGetRanged("member", out _, out var low, out _, out var isFinal));
        Assert.Equal(1500, low);
        Assert.True(isFinal);
    }

    [Fact]
    public void AbsentAttributeReportsNotFound()
    {
        var entry = FakeLdapExecutor.Entry("CN=None,DC=corp", ("cn", "None"));
        Assert.False(entry.TryGetRanged("member", out var values, out _, out _, out _));
        Assert.Empty(values);
    }

    [Theory]
    [InlineData("member;range=0-1499", true, "member", 0, 1499, false)]
    [InlineData("member;Range=1500-*", true, "member", 1500, -1, true)]
    [InlineData("memberOf;range=0-999", true, "memberOf", 0, 999, false)]
    [InlineData("member", false, null, 0, -1, false)]
    [InlineData("member;binary", false, null, 0, -1, false)]
    [InlineData("member;range=", false, null, 0, -1, false)]
    [InlineData("member;range=abc-def", false, null, 0, -1, false)]
    [InlineData("", false, null, 0, -1, false)]
    public void RangeOptionParsing(
        string input, bool expected, string? baseName, int low, int high, bool isFinal)
    {
        var ok = LdapEntry.TryParseRangeOption(input, out var actualBase, out var actualLow, out var actualHigh, out var actualFinal);

        Assert.Equal(expected, ok);
        if (!expected) return;

        Assert.Equal(baseName, actualBase);
        Assert.Equal(low, actualLow);
        Assert.Equal(high, actualHigh);
        Assert.Equal(isFinal, actualFinal);
    }
}
