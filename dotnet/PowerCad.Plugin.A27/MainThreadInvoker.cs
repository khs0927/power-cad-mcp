using System.Windows.Forms;
using PowerCad.Core;

namespace PowerCad.Plugin;

/// <summary>
/// Runs work on AutoCAD's main (UI) thread. The AutoCAD API is not thread-safe, while pipe requests
/// arrive on background threads; a hidden control created on the main thread during Initialize() gives
/// a reliable BeginInvoke target (the same role ExternalEvent plays in the Revit add-in of bimwright).
/// </summary>
internal sealed class MainThreadInvoker : IDisposable
{
    private const int Queued = 0;
    private const int Started = 1;
    private const int Abandoned = 2;

    private readonly Control _control;

    public MainThreadInvoker()
    {
        _control = new Control();
        _control.CreateControl();
        _ = _control.Handle; // force the window handle on the main thread
    }

    /// <summary>
    /// Queues <paramref name="work"/> and waits up to <paramref name="timeout"/> for it to START.
    /// Work that has not started by then is cancelled and never runs. Work that has started is always
    /// awaited to completion, so an edit can never commit after the caller was told it failed.
    /// Exceptions from <paramref name="work"/> are rethrown unwrapped (CadException codes survive).
    /// </summary>
    public T Invoke<T>(Func<T> work, TimeSpan timeout)
    {
        if (!_control.InvokeRequired)
        {
            return work();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = Queued;
        _control.BeginInvoke(() =>
        {
            if (Interlocked.CompareExchange(ref state, Started, Queued) != Queued)
            {
                return; // the caller gave up before we started; do not apply a stale edit
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });

        // IAsyncResult.WaitOne does not observe a fault, unlike Task.Wait (which throws AggregateException).
        var waitHandle = ((IAsyncResult)tcs.Task).AsyncWaitHandle;
        if (!waitHandle.WaitOne(timeout)
            && Interlocked.CompareExchange(ref state, Abandoned, Queued) == Queued)
        {
            throw new CadException(
                ErrorCodes.Busy,
                "AutoCAD's main thread did not pick up the request in time; nothing was changed.",
                "AutoCAD is probably running a command or showing a dialog. Finish or cancel it (Esc), then re-query and retry.");
        }

        // Either finished in time, or already running: wait for the real outcome.
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose() => _control.Dispose();
}
