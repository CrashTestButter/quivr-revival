using System.Reflection;
using QuiVrRevival.Installer;
using QuiVrRevival.Server;

namespace QuiVrRevival;

public static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>Where the server keeps its database: %APPDATA%\QuiVrRevival, ~/.local/share/QuiVrRevival.</summary>
    public static string DefaultDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows() ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.LocalApplicationData),
        "QuiVrRevival");

    const string Usage = """
        QuiVr Revival - replacement server and installer for QuiVr.

        Run without arguments for the interactive menu.

          serve [--port 4040] [--host 127.0.0.1] [--data DIR] [--parent-pid PID] [--quiet]
              Run the server. Environment variables QUIVR_PORT, QUIVR_HOST, QUIVR_DATA set defaults.
          install [--game DIR] (--local [--port N] | --server ADDRESS)
              Patch the game to use a local server (started with the game) or a remote one.
          uninstall [--game DIR]
              Restore the original game files.
          status [--game DIR]
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            // Started by double-click: never let the window vanish without a message.
            try
            {
                return await Menu.Run();
            }
            catch (Exception e)
            {
                Console.WriteLine($"  Something went wrong: {e.Message}");
                Console.WriteLine($"  Details: {e}");
                Menu.Pause();
                return 1;
            }
        }

        var opts = ParseOptions(args.Skip(1));
        try
        {
            switch (args[0])
            {
                case "serve":
                    return await ServerHost.Run(new ServerOptions(
                        int.Parse(opts.GetValueOrDefault("port") ?? Environment.GetEnvironmentVariable("QUIVR_PORT") ?? ServerHost.DefaultPort.ToString()),
                        opts.GetValueOrDefault("host") ?? Environment.GetEnvironmentVariable("QUIVR_HOST") ?? "127.0.0.1",
                        opts.GetValueOrDefault("data") ?? Environment.GetEnvironmentVariable("QUIVR_DATA") ?? DefaultDataDir,
                        opts.TryGetValue("parent-pid", out var pid) && int.TryParse(pid, out var p) ? p : null,
                        opts.ContainsKey("quiet")));
                case "install":
                    {
                        var game = FindGame(opts);
                        if (opts.GetValueOrDefault("server") is { } address)
                        {
                            string url = ModInstaller.NormalizeServerAddress(address) ?? throw new InstallError($"Not a server address: {address}");
                            ModInstaller.InstallRemote(game, url);
                            Console.WriteLine($"Installed. QuiVr uses {url}");
                        }
                        else
                        {
                            int port = int.Parse(opts.GetValueOrDefault("port") ?? ServerHost.DefaultPort.ToString());
                            bool autostart = ModInstaller.InstallLocal(game, port);
                            Console.WriteLine($"Installed. QuiVr uses {ModInstaller.LocalUrl(port)}" + (autostart ? ", started with the game." : "; start the server yourself."));
                        }
                        return 0;
                    }
                case "uninstall":
                    ModInstaller.Uninstall(FindGame(opts));
                    Console.WriteLine("Original game files restored.");
                    return 0;
                case "status":
                    {
                        var game = FindGame(opts);
                        var cfg = game.ReadConfig();
                        Console.WriteLine($"Game:      {game.Root}");
                        Console.WriteLine($"Installed: {GamePatcher.IsInstalled(game)}");
                        if (cfg != null) Console.WriteLine($"Server:    {cfg.ServerUrl} (autostart: {cfg.AutoStart})");
                        return 0;
                    }
                case "import-parse" when args.Length > 1:
                    return Importer.Run(args[1], opts.GetValueOrDefault("data") ?? DefaultDataDir);
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                default:
                    Console.WriteLine(Usage);
                    return args[0] is "help" or "--help" or "-h" ? 0 : 1;
            }
        }
        catch (InstallError e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    static GameDir FindGame(Dictionary<string, string?> opts) =>
        GameDir.Find(opts.GetValueOrDefault("game"))
        ?? throw new InstallError("QuiVr was not found. Pass the game folder with --game.");

    static Dictionary<string, string?> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string?>();
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--"))
            {
                pending = arg[2..];
                result[pending] = null;
            }
            else if (pending != null)
            {
                result[pending] = arg;
                pending = null;
            }
        }
        return result;
    }
}
