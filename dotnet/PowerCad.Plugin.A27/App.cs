using System.Text.Json.Nodes;
using Autodesk.AutoCAD.Runtime;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Transport;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(PowerCad.Plugin.App))]
[assembly: CommandClass(typeof(PowerCad.Plugin.PluginCommands))]

namespace PowerCad.Plugin;

/// <summary>
/// Loaded by AutoCAD 2027 (autoloader bundle or NETLOAD). Starts a per-user named-pipe listener and
/// publishes a discovery file (%LOCALAPPDATA%\PowerCad\autocad-2027-PID.json) with a random token.
/// </summary>
public sealed class App : IExtensionApplication
{
    internal static App? Current { get; private set; }

    private MainThreadInvoker? _invoker;
    private PipeServer? _server;
    private DiscoveryInfo? _info;
    private readonly DiscoveryStore _store = new();

    internal string Status => _info is null
        ? "Power CAD: listener stopped."
        : $"Power CAD {_info.PluginVersion}: listening on pipe '{_info.PipeName}' (target {_info.Target}).";

    public void Initialize()
    {
        Current = this;
        try
        {
            Start();
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n{Status}\n");
        }
        catch (System.Exception e)
        {
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nPower CAD failed to start: {e.Message}\n");
        }
    }

    internal void Start()
    {
        if (_server is not null)
        {
            return;
        }

        _invoker ??= new MainThreadInvoker();
        var readOnly = Environment.GetEnvironmentVariable("POWER_CAD_READ_ONLY") is "1" or "true";
        var dispatcher = new CommandDispatcher(new AcadDocument(_invoker, BusyTimeout()), new DispatcherOptions { ReadOnly = readOnly });
        var pid = Environment.ProcessId;
        _info = new DiscoveryInfo(
            SchemaVersion: 1,
            Product: "autocad",
            Year: 2027,
            PipeName: $"powercad-a27-{pid}-{Guid.NewGuid():N}",
            AuthToken: DiscoveryStore.NewToken(),
            Pid: pid,
            PluginVersion: typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            StartedAt: DateTimeOffset.UtcNow);

        // The dispatcher blocks while the main thread works, so run it off the pipe's I/O thread.
        _server = new PipeServer(_info.PipeName, _info.AuthToken, (command, parameters, ct) =>
            Task.Run(() => dispatcher.Execute(command, parameters), ct));
        _server.Start();
        _store.Write(_info);
    }

    /// <summary>
    /// How long a request may wait for AutoCAD's main thread before failing with CAD_BUSY. Kept well
    /// below common MCP client/bridge timeouts (~60 s) so the agent gets a clear answer instead of a
    /// dropped call. POWER_CAD_BUSY_TIMEOUT (seconds, 3-300) overrides it.
    /// </summary>
    private static TimeSpan BusyTimeout() =>
        int.TryParse(Environment.GetEnvironmentVariable("POWER_CAD_BUSY_TIMEOUT"), out var s) && s is >= 3 and <= 300
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(20);

    internal void Stop()
    {
        if (_info is not null)
        {
            _store.Delete(_info);
        }

        _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _server = null;
        _info = null;
    }

    public void Terminate()
    {
        Stop();
        _invoker?.Dispose();
        Current = null;
    }
}

public sealed class PluginCommands
{
    [CommandMethod("POWERCAD_STATUS")]
    public void StatusCommand() => Write(App.Current?.Status ?? "Power CAD is not loaded.");

    [CommandMethod("POWERCAD_RESTART")]
    public void RestartCommand()
    {
        if (App.Current is not { } app)
        {
            return;
        }

        app.Stop();
        app.Start();
        Write(app.Status);
    }

    [CommandMethod("POWERCAD_STOP")]
    public void StopCommand()
    {
        App.Current?.Stop();
        Write("Power CAD listener stopped. POWERCAD_RESTART starts it again.");
    }

    private static void Write(string message) =>
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n{message}\n");

    internal static JsonObject Info() => new() { ["status"] = App.Current?.Status };
}
