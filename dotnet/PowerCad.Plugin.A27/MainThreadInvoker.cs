using System.Windows.Forms;
using PowerCad.Core;

namespace PowerCad.Plugin;

/// <summary>
/// Runs work on AutoCAD's main (UI) thread. The AutoCAD API is not thread-safe, while pipe requests
/// arrive on background threads; a hidden control created on the main thread during Initialize() gives
/// a reliable BeginInvoke target (the same role ExternalEvent plays in the Revit add-in of bimwright).
/// Work that is still queued when the caller times out is skipped, never executed late.
/// </summary>
internal sealed class MainThreadInvoker : IDisposable
{
    private readonly Control _control;

    public MainThreadInvoker()
    {
        _control = new Control();
        _control.CreateControl();
        _ = _control.Handle; // force the window handle on the main thread
    }

    public T Invoke<T>(Func<T> work, TimeSpan timeout)
    {
        if (!_control.InvokeRequired)
        {
            return work();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = 0;
        _control.BeginInvoke(() =>
        {
            if (Interlocked.CompareExchange(ref abandoned, 0, 0) == 1)
            {
                return; // the caller gave up; do not apply a stale edit
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

        if (!tcs.Task.Wait(timeout))
        {
            Interlocked.Exchange(ref abandoned, 1);
            if (!tcs.Task.IsCompleted)
            {
                throw new CadException(
                    ErrorCodes.Busy,
                    "AutoCAD's main thread did not pick up the request in time.",
                    "AutoCAD is probably running a command or showing a dialog. Finish or cancel it (Esc), then re-query and retry.");
            }
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose() => _control.Dispose();
}
