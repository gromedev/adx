using System.Collections.Concurrent;
using System.Management.Automation;

namespace ADx.Cmdlets.Base;

/// <summary>
/// Protocol-neutral base for all ADx cmdlets. Owns cancellation, disposal, and the
/// background-thread message buffer — everything not tied to LDAP itself.
/// <para>
/// This duplicates the equivalent ~55 lines in the Mgx module rather than sharing an assembly
/// with it. That is deliberate. Two modules shipping different versions of one common library
/// into the same PowerShell session is the diamond problem: whichever loads first wins, and the
/// other can fail at JIT time with a TypeLoadException that points nowhere useful. ADx therefore
/// depends on nothing from Mgx, and the two can version independently.
/// </para>
/// </summary>
public abstract class ADxCmdletCore : PSCmdlet, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _cancellationToken;
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked for thread safety)

    protected ADxCmdletCore()
    {
        // Cached at construction, NOT read through _cts.Token on demand: StopProcessing
        // disposes the CTS on the stopping thread while the pipeline thread is still
        // unwinding its OperationCanceledException, and reading _cts.Token there throws
        // ObjectDisposedException INSIDE the `when (CancellationToken.IsCancellationRequested)`
        // catch filters -- a throwing filter is silently false, so the clean "Search
        // cancelled" path fell through to the generic error handler. A copied token stays
        // valid after its source is disposed.
        _cancellationToken = _cts.Token;
    }

    protected CancellationToken CancellationToken => _cancellationToken;

    #region Lifecycle

    protected override void StopProcessing()
    {
        // Cancel can race EndProcessing's Dispose; if that already won, there is nothing
        // in flight left to cancel and the disposed CTS is fine to ignore.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        Dispose();
    }

    protected override void EndProcessing()
    {
        Dispose();
    }

    /// <summary>
    /// Subclass hook for releasing transport-specific resources (the LDAP connection).
    /// Called exactly once, inside the same Interlocked guard that protects <see cref="Dispose"/>.
    /// </summary>
    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        // Thread-safe: StopProcessing (pipeline-stopping thread) and EndProcessing (pipeline thread)
        // can race. Interlocked ensures only one thread enters the dispose body.
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _cts.Cancel();
            _cts.Dispose();
            DisposeCore();
        }
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Background-thread message buffering

    // Connection and paging callbacks fire on thread pool threads, where WriteVerbose and
    // WriteWarning are illegal — calling them off the pipeline thread throws. Messages are
    // queued here and drained on the pipeline thread instead.
    private readonly ConcurrentQueue<(bool IsWarning, string Message)> _messages = new();

    protected void EnqueueVerbose(string message) => _messages.Enqueue((false, message));

    protected void EnqueueWarning(string message) => _messages.Enqueue((true, message));

    /// <summary>
    /// Drain buffered messages. MUST be called on the pipeline thread
    /// (i.e. after .GetAwaiter().GetResult() returns).
    /// </summary>
    protected void DrainMessages()
    {
        while (_messages.TryDequeue(out var m))
        {
            if (m.IsWarning) WriteWarning(m.Message);
            else WriteVerbose(m.Message);
        }
    }

    #endregion
}
