using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Workbench;

/// <summary>Carries every MCP tool call onto the app's UI thread (the WPF dispatcher), where the window's own handlers
/// run, so a call and a click are the same call on the same state. A call the UI thread does not take up within
/// <see cref="Wait"/> (it is rebuilding a scene, which blocks it) is withdrawn and answered with one sentence.</summary>
internal sealed class StateHub
{
    readonly Dispatcher _ui;
    internal IWorkbenchSurface Surface { get; }
    internal TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>The launcher's state and the houseCARL server bound to its profile (manual mode).</summary>
    internal Launcher Launcher { get; init; } = new();
    /// <summary>Each decided row's standing as last compared, by row key; reset when a decision is made or a bundle opens.</summary>
    internal Dictionary<string, DecisionMatch> Standings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    internal StateHub(Dispatcher ui, IWorkbenchSurface surface) { _ui = ui; Surface = surface; }

    internal async Task<CallToolResult> OnUi(Func<Task<CallToolResult>> call)
    {
        var op = _ui.InvokeAsync(call);
        var first = await Task.WhenAny(op.Task, Task.Delay(Wait));
        if (first != op.Task && op.Status == DispatcherOperationStatus.Pending && op.Abort())
        {
            // Read off the UI thread on purpose: the UI thread is the one that is busy. A string field; a stale read
            // only changes the wording.
            string? loading = Surface.Loading;
            return StateTools.Refuse(loading is not null
                ? $"the window is rebuilding the scene for cell {loading} and does not answer until it is built; call again in a minute."
                : FormattableString.Invariant($"the window did not take the call up within {Wait.TotalSeconds:0} s because its thread is busy; call again shortly."));
        }
        return await await op.Task;
    }
}

/// <summary>The app's localhost MCP server (ModelContextProtocol.AspNetCore 2.2.0, Streamable HTTP, stateless, as
/// houseCARL's own server runs): <c>workbench_state</c> and <c>workbench_act</c> (<see cref="StateTools"/>).</summary>
internal sealed class StateServer : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly StateHub _hub;
    internal string Url { get; }

    StateServer(WebApplication app, string url, StateHub hub) { _app = app; Url = url; _hub = hub; }

    internal static async Task<StateServer> StartAsync(int port, StateHub hub)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
        b.Logging.ClearProviders();
        b.Services.AddSingleton(hub);
        b.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new Implementation { Name = "workbench", Version = "0.5" };
            o.ServerInstructions = StateTools.Instructions;
        })
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<StateTools>()
        // the houseCARL tools of the server bound to the launcher's profile, forwarded under the manual-mode write rule
        .WithTools(await ForwardedTool.AllAsync(SafeServerExe()));
        var url = $"http://127.0.0.1:{port}";
        b.WebHost.UseUrls(url);
        var app = b.Build();
        app.MapMcp();
        await app.StartAsync();
        return new StateServer(app, url, hub);
    }

    static string SafeServerExe() { try { return Program.ServerExe(); } catch (InvalidOperationException) { return ""; } }

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); await _hub.Launcher.DisposeAsync(); }
}
