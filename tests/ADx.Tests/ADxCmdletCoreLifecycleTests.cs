using ADx.Cmdlets.Base;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// The 0.3.0 Ctrl-C/dispose race fix, pinned. The invariant is subtle and guarded only by
/// comments: the token is cached at construction because reading <c>_cts.Token</c> AFTER
/// StopProcessing disposed the source throws ObjectDisposedException INSIDE the
/// <c>when (CancellationToken.IsCancellationRequested)</c> catch filters -- and a throwing
/// filter is silently false, so the clean "Search cancelled" path fell through to the
/// generic error handler. An innocent "simplification" back to <c>_cts.Token</c> reintroduces
/// that across all 17 cmdlets with no compile-time signal; these tests are the signal.
/// The race's loser-side states reproduce sequentially -- no threads, no pipeline needed.
/// </summary>
public class ADxCmdletCoreLifecycleTests
{
    private sealed class Probe : ADxCmdletCore
    {
        public CancellationToken Token => CancellationToken;
        public void InvokeStopProcessing() => StopProcessing();
    }

    [Fact]
    public void CancellationToken_SurvivesDisposal_AndReadsCancelled()
    {
        // Dispose cancels before disposing, so the cached token must (a) not throw on read
        // -- the pre-fix failure -- and (b) deterministically observe the cancellation, which
        // is what routes the unwinding OperationCanceledException into the clean path.
        var probe = new Probe();
        var token = probe.Token;

        probe.Dispose();

        Assert.True(token.IsCancellationRequested);
        Assert.True(probe.Token.IsCancellationRequested);
    }

    [Fact]
    public void StopProcessing_AfterDispose_DoesNotThrow()
    {
        // The other side of the race: EndProcessing's Dispose already won, StopProcessing's
        // Cancel hits a disposed CTS. The catch at that site must swallow exactly this.
        var probe = new Probe();
        probe.Dispose();

        probe.InvokeStopProcessing();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var probe = new Probe();
        probe.Dispose();
        probe.Dispose();
    }
}
