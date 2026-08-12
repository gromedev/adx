using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M6: completing range-limited attributes via follow-up base-scope reads. The fake scripts
/// the server's side of the range walk: request "member;range=1500-*", answer
/// "member;range=1500-2999" (more remains) or "member;range=1500-*" (final chunk).
/// </summary>
public class LdapRangeRetrieverTests
{
    private static LdapEntry Entry(string dn, params (string Name, IReadOnlyList<object> Values)[] attributes)
    {
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in attributes) dict[name] = values;
        return new LdapEntry(dn, dict);
    }

    private static IReadOnlyList<object> Members(int from, int toInclusive)
    {
        var list = new List<object>();
        for (var i = from; i <= toInclusive; i++) list.Add($"CN=m{i},DC=corp");
        return list;
    }

    [Fact]
    public void NeedsCompletion_TrueOnlyForPartialRanges()
    {
        Assert.False(LdapRangeRetriever.NeedsCompletion(
            Entry("CN=A,DC=corp", ("member", Members(0, 2)))));
        Assert.False(LdapRangeRetriever.NeedsCompletion(
            Entry("CN=A,DC=corp", ("member;range=1500-*", Members(1500, 1501)))));
        Assert.True(LdapRangeRetriever.NeedsCompletion(
            Entry("CN=A,DC=corp", ("member;range=0-1499", Members(0, 1499)))));
    }

    [Fact]
    public async Task EntryWithoutRanges_IsReturnedUnchanged()
    {
        var entry = Entry("CN=Small,DC=corp", ("member", Members(0, 2)));
        var fake = new FakeLdapExecutor(Array.Empty<LdapPage>());

        var result = await LdapRangeRetriever.CompleteAsync(fake, entry, CancellationToken.None);

        Assert.Same(entry, result);
        Assert.Equal(0, fake.ReadEntryCallCount);
    }

    [Fact]
    public async Task SingleFollowUp_CompletesTheAttribute()
    {
        // Initial read carried 0-2; the follow-up "member;range=3-*" answers with the final
        // chunk 3-4.
        var initial = Entry("CN=Big,DC=corp",
            ("cn", new object[] { "Big" }),
            ("member;range=0-2", Members(0, 2)));

        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, attrs) =>
            {
                Assert.Equal("CN=Big,DC=corp", dn);
                Assert.Equal("member;range=3-*", Assert.Single(attrs));
                return Entry(dn, ("member;range=3-*", Members(3, 4)));
            });

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.Equal(1, fake.ReadEntryCallCount);
        // Completed values live under the BASE name; the ranged key is gone.
        Assert.False(result.Attributes.ContainsKey("member;range=0-2"));
        Assert.Equal(5, result.GetStrings("member").Count);
        Assert.Equal("CN=m0,DC=corp", result.GetStrings("member")[0]);
        Assert.Equal("CN=m4,DC=corp", result.GetStrings("member")[4]);
        // Untouched attributes survive the rebuild.
        Assert.Equal("Big", result.GetString("cn"));
    }

    [Fact]
    public async Task MultipleChunks_AreWalkedInOrder()
    {
        var initial = Entry("CN=Huge,DC=corp", ("member;range=0-1", Members(0, 1)));

        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, attrs) => attrs[0] switch
            {
                "member;range=2-*" => Entry(dn, ("member;range=2-3", Members(2, 3))),
                "member;range=4-*" => Entry(dn, ("member;range=4-*", Members(4, 5))),
                _ => throw new InvalidOperationException($"Unexpected range request '{attrs[0]}'")
            });

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.Equal(2, fake.ReadEntryCallCount);
        Assert.Equal(6, result.GetStrings("member").Count);
    }

    [Fact]
    public async Task ServerAnsweringWithThePlainAttribute_EndsTheWalk()
    {
        var initial = Entry("CN=G,DC=corp", ("member;range=0-1", Members(0, 1)));

        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("member", Members(2, 2))));

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.Equal(1, fake.ReadEntryCallCount);
        Assert.Equal(3, result.GetStrings("member").Count);
    }

    [Fact]
    public async Task ObjectVanishingMidWalk_KeepsWhatWasFetched()
    {
        var initial = Entry("CN=G,DC=corp", ("member;range=0-1", Members(0, 1)));
        var fake = new FakeLdapExecutor(Array.Empty<LdapPage>(), readEntry: (_, _) => null);

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.Equal(2, result.GetStrings("member").Count);
        Assert.Equal(1, fake.ReadEntryCallCount);
    }

    [Fact]
    public async Task NonProgressingServer_DoesNotLoopForever()
    {
        // A chunk whose high never advances would otherwise spin; the retriever stops with
        // what it has rather than hang the pipeline.
        var initial = Entry("CN=G,DC=corp", ("member;range=0-1", Members(0, 1)));
        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("member;range=0-1", Members(0, 1))));

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.True(fake.ReadEntryCallCount <= 2,
            $"Expected the non-progress guard to trip, saw {fake.ReadEntryCallCount} reads.");
        Assert.Equal(4, result.GetStrings("member").Count); // initial 2 + the one echoed chunk
    }

    [Fact]
    public async Task MultipleRangedAttributes_AreEachCompleted()
    {
        var initial = Entry("CN=G,DC=corp",
            ("member;range=0-0", Members(0, 0)),
            ("memberOf;range=0-0", new object[] { "CN=P0,DC=corp" }));

        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, attrs) => attrs[0] switch
            {
                "member;range=1-*" => Entry(dn, ("member;range=1-*", Members(1, 1))),
                "memberOf;range=1-*" => Entry(dn, ("memberOf;range=1-*", new object[] { "CN=P1,DC=corp" })),
                _ => null
            });

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.Equal(2, result.GetStrings("member").Count);
        Assert.Equal(2, result.GetStrings("memberOf").Count);
    }

    // ---- truncation must be announced, never silent ----

    [Fact]
    public async Task ObjectVanishingMidWalk_Warns()
    {
        // Every early exit returns a PARTIAL value set. Returning it silently is the exact
        // "acted on the wrong set with no signal" failure the design guards against, so each
        // guard path has to say so.
        var initial = Entry("CN=G,DC=corp", ("member;range=0-1", Members(0, 1)));
        var fake = new FakeLdapExecutor(Array.Empty<LdapPage>(), readEntry: (_, _) => null);

        var warnings = new List<string>();
        await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None, warnings.Add);

        var warning = Assert.Single(warnings);
        Assert.Contains("disappeared", warning);
        Assert.Contains("may not be the full set", warning);
    }

    [Fact]
    public async Task NonProgressingServer_Warns()
    {
        var initial = Entry("CN=G,DC=corp", ("member;range=0-1", Members(0, 1)));
        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("member;range=0-1", Members(0, 1))));

        var warnings = new List<string>();
        await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None, warnings.Add);

        Assert.Contains(warnings, w => w.Contains("stopped making progress"));
    }

    [Fact]
    public async Task ServerAnsweringWithoutTheAttribute_Warns()
    {
        var initial = Entry("CN=G,DC=corp", ("member;range=0-1", Members(0, 1)));
        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("cn", new object[] { "G" })));

        var warnings = new List<string>();
        await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None, warnings.Add);

        Assert.Contains(warnings, w => w.Contains("without the attribute"));
    }

    [Fact]
    public async Task SuccessfulWalk_DoesNotWarn()
    {
        // The normal path completes with the server's own "*" terminator; warning there would
        // train the operator to ignore the ones that matter.
        var initial = Entry("CN=Big,DC=corp", ("member;range=0-2", Members(0, 2)));
        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("member;range=3-*", Members(3, 4))));

        var warnings = new List<string>();
        var result = await LdapRangeRetriever.CompleteAsync(
            fake, initial, CancellationToken.None, warnings.Add);

        Assert.Empty(warnings);
        Assert.Equal(5, result.GetStrings("member").Count);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var initial = Entry("CN=G,DC=corp", ("member;range=0-1", Members(0, 1)));
        var fake = new FakeLdapExecutor(Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("member;range=2-3", Members(2, 3))));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LdapRangeRetriever.CompleteAsync(fake, initial, cts.Token));
    }

    // ---- the empty-sibling artifact ----
    // For a group past MaxValRange, AD returns BOTH "member;range=0-1499" (the values) AND
    // a plain "member" with ZERO values. Which key a consumer met first depended on
    // hashtable enumeration order, which .NET randomises PER PROCESS -- a 1,700-member
    // group read as empty in roughly half of all processes. LdapEntry's constructor now
    // drops the artifact; CompleteAsync additionally never lets a plain sibling overwrite a
    // completed walk. Both are asserted in BOTH insertion orders.

    [Theory]
    [InlineData(true)]   // plain empty sibling inserted BEFORE the ranged key
    [InlineData(false)]  // ...and AFTER it
    public void Ctor_DropsTheEmptyPlainSiblingOfARangedKey(bool plainFirst)
    {
        var entry = plainFirst
            ? Entry("CN=Big,DC=corp", ("member", Array.Empty<object>()), ("member;range=0-1499", Members(0, 1499)))
            : Entry("CN=Big,DC=corp", ("member;range=0-1499", Members(0, 1499)), ("member", Array.Empty<object>()));

        Assert.False(entry.Attributes.ContainsKey("member"));
        Assert.True(entry.Attributes.ContainsKey("member;range=0-1499"));

        // Ranged-aware reads see the first page, flagged incomplete -- never "empty".
        Assert.True(entry.TryGetRanged("member", out var values, out _, out var high, out var isFinal));
        Assert.Equal(1500, values.Count);
        Assert.Equal(1499, high);
        Assert.False(isFinal);
    }

    [Fact]
    public void Ctor_KeepsGenuinelyEmptyAttributes_AndNonEmptySiblings()
    {
        // Empty with NO ranged sibling: real data (attribute present, no values) -- kept.
        var empty = Entry("CN=A,DC=corp", ("description", Array.Empty<object>()));
        Assert.True(empty.Attributes.ContainsKey("description"));

        // Non-empty plain next to a ranged key: never observed from AD, but dropping VALUES
        // would be data loss -- kept.
        var both = Entry("CN=B,DC=corp",
            ("member", Members(0, 1)),
            ("member;range=0-1499", Members(0, 1499)));
        Assert.True(both.Attributes.ContainsKey("member"));
        Assert.True(both.Attributes.ContainsKey("member;range=0-1499"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompleteAsync_PlainSiblingNeverClobbersTheWalk(bool plainFirst)
    {
        // A NON-empty plain sibling (the empty one is gone at construction) must not
        // overwrite the completed walk regardless of enumeration order.
        var initial = plainFirst
            ? Entry("CN=Big,DC=corp", ("member", Members(0, 1)), ("member;range=0-2", Members(0, 2)))
            : Entry("CN=Big,DC=corp", ("member;range=0-2", Members(0, 2)), ("member", Members(0, 1)));

        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("member;range=3-*", Members(3, 4))));

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.Equal(5, result.GetStrings("member").Count);
        Assert.Equal("CN=m4,DC=corp", result.GetStrings("member")[4]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompleteAsync_TheWireShape_YieldsTheFullSetInEitherOrder(bool plainFirst)
    {
        // End to end over the exact dual shape AD sends: empty plain + ranged first page.
        var initial = plainFirst
            ? Entry("CN=Big,DC=corp", ("member", Array.Empty<object>()), ("member;range=0-2", Members(0, 2)))
            : Entry("CN=Big,DC=corp", ("member;range=0-2", Members(0, 2)), ("member", Array.Empty<object>()));

        var fake = new FakeLdapExecutor(
            Array.Empty<LdapPage>(),
            readEntry: (dn, _) => Entry(dn, ("member;range=3-*", Members(3, 4))));

        var result = await LdapRangeRetriever.CompleteAsync(fake, initial, CancellationToken.None);

        Assert.Equal(5, result.GetStrings("member").Count);
        Assert.False(result.Attributes.Keys.Any(k => k.Contains(";range=", StringComparison.OrdinalIgnoreCase)));
    }
}
