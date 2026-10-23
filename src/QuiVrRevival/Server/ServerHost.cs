using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace QuiVrRevival.Server;

public sealed record ServerOptions(int Port, string Host, string DataDir, int? ParentPid, bool Quiet);

public static class ServerHost
{
    public const int DefaultPort = 4040;

    public static async Task<int> Run(ServerOptions o, CancellationToken stop = default)
    {
        Directory.CreateDirectory(o.DataDir);
        Log.Init(o.DataDir);
        Log.Console = !o.Quiet;

        if (!IPAddress.TryParse(o.Host, out var address))
        {
            Log.Info($"Invalid listen address: {o.Host}");
            return 1;
        }

        Store store;
        try
        {
            store = new Store(Path.Combine(o.DataDir, "quivr.db"));
        }
        catch (IOException)
        {
            Log.Info($"The database in {o.DataDir} is in use - is another QuiVr Revival server already running?");
            return 1;
        }

        using (store)
        {
            var catalog = new Catalog(o.DataDir);
            var api = new ParseApi(store, catalog, new GameConfig(o.DataDir), new GameRules(store, catalog));

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 1_000_000;
                k.Listen(address, o.Port);
            });
            var app = builder.Build();
            app.Map("/parse/{**path}", ctx => api.Handle(ctx, ctx.Request.RouteValues["path"] as string ?? ""));
            app.Map("/parse", ctx => api.Handle(ctx, ""));
            app.MapGet("/", () => "QuiVr Revival server is running. Point the game at /parse/.");

            try
            {
                await app.StartAsync(stop);
            }
            catch (IOException e) when (e.InnerException is SocketException || e is IOException)
            {
                Log.Info($"Could not listen on {o.Host}:{o.Port} - is the port already in use? ({e.Message})");
                return 1;
            }

            Log.Info($"QuiVr Revival server {Program.Version} listening on http://{o.Host}:{o.Port}/parse/");
            Log.Info($"Data folder: {o.DataDir} ({store.Count("_User")} players)");

            var stopped = new TaskCompletionSource();
            using var reg = stop.Register(() => stopped.TrySetResult());
            app.Lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
            if (o.ParentPid is int pid) _ = WatchParent(pid, stopped);

            await stopped.Task;
            Log.Info("Server stopping.");
            await app.StopAsync();
            return 0;
        }
    }

    // Started by the game: stop when the game exits.
    static async Task WatchParent(int pid, TaskCompletionSource stopped)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            while (!parent.HasExited) await Task.Delay(2000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        Log.Info("Game closed.");
        stopped.TrySetResult();
    }

    // Docker, VM and VPN-style adapters that other players can't reach.
    static readonly string[] VirtualAdapterPrefixes = ["docker", "br-", "veth", "virbr", "vEthernet", "VirtualBox", "VMware", "tailscale", "zt"];

    /// <summary>LAN addresses to show a host, so they can tell other players where to connect.</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && !VirtualAdapterPrefixes.Any(prefix => n.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
