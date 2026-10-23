using QuiVrRevival.Server;

namespace QuiVrRevival.Installer;

/// <summary>What people see when they double-click the program.</summary>
public static class Menu
{
    public static async Task<int> Run()
    {
        Console.Title = "QuiVr Revival";
        Header();
        var game = GameDir.Find();
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine(game == null ? "  QuiVr was not found automatically." : $"  Game:   {game.Root}");
            if (game != null) Console.WriteLine($"  Status: {Status(game)}");
            Console.WriteLine();
            Console.WriteLine("  1  Play on this PC              (recommended - progress is saved on this computer)");
            Console.WriteLine("  2  Play on someone's server     (you need their address)");
            Console.WriteLine("  3  Host a server for others     (keep this window open while they play)");
            Console.WriteLine("  4  Uninstall                    (restore the original game files)");
            Console.WriteLine("  0  Exit");
            Console.WriteLine();
            string choice = Ask("Choose", "1");
            try
            {
                switch (choice)
                {
                    case "1":
                        game ??= AskGameDir();
                        if (game != null) PlayLocal(game);
                        break;
                    case "2":
                        game ??= AskGameDir();
                        if (game != null) await PlayRemote(game);
                        break;
                    case "3":
                        return await Host();
                    case "4":
                        game ??= AskGameDir();
                        if (game != null) Uninstall(game);
                        break;
                    case "0":
                        return 0;
                    default:
                        Console.WriteLine("  Please type a number from the list.");
                        break;
                }
            }
            catch (InstallError e)
            {
                Error(e.Message);
            }
            catch (UnauthorizedAccessException e)
            {
                Error($"No permission to change the game files: {e.Message}\n  Close QuiVr if it is running, and try again.");
            }
            catch (IOException e)
            {
                Error($"Could not change the game files: {e.Message}\n  Close QuiVr if it is running, and try again.");
            }
        }
    }

    static void Header()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  QuiVr Revival {Program.Version}");
        Console.ResetColor();
        Console.WriteLine("  Brings back saving items, fragments and leaderboards in QuiVr.");
    }

    static string Status(GameDir game)
    {
        if (!GamePatcher.IsInstalled(game)) return "not installed";
        var cfg = game.ReadConfig();
        if (cfg == null) return "installed (no settings found - choose 1 or 2 again)";
        return cfg.AutoStart ? "installed - playing on this PC" : $"installed - using server {cfg.ServerUrl}";
    }

    static void PlayLocal(GameDir game)
    {
        bool autostart = ModInstaller.InstallLocal(game);
        Success("Installed.");
        if (autostart)
        {
            Console.WriteLine("  Start QuiVr from Steam as usual. The server starts and stops together with the game.");
            Console.WriteLine($"  Your progress is saved in: {game.LocalProgressDir}");
        }
        else
        {
            Console.WriteLine($"  The game will use a server on this computer ({ModInstaller.LocalUrl(ServerHost.DefaultPort)}).");
            Console.WriteLine("  Start it before playing: \"QuiVrRevival serve\" or the Docker image (see README).");
        }
    }

    static async Task PlayRemote(GameDir game)
    {
        Console.WriteLine("  Enter the server address you were given, e.g. 192.168.1.20 or quivr.example.com:4040");
        string? url = ModInstaller.NormalizeServerAddress(Ask("Address", ""));
        if (url == null)
        {
            Error("That does not look like a server address.");
            return;
        }
        Console.WriteLine($"  Checking {url} ...");
        if (await ModInstaller.CheckServer(url))
        {
            Success("Server found.");
        }
        else
        {
            Warn("No QuiVr Revival server answered at that address right now.");
            if (!Ask("Use it anyway? (y/n)", "n").StartsWith('y')) return;
        }
        ModInstaller.InstallRemote(game, url);
        Success("Installed. Start QuiVr from Steam as usual.");
    }

    static void Uninstall(GameDir game)
    {
        ModInstaller.Uninstall(game);
        Success("The original game files are restored.");
        Console.WriteLine($"  Your saved progress is kept in: {game.LocalProgressDir}");
    }

    static async Task<int> Host()
    {
        Console.WriteLine();
        Console.WriteLine("  Starting the server for other players...");
        var addresses = ServerHost.LocalAddresses().ToList();
        Console.WriteLine();
        Console.WriteLine("  Players on your home network enter this address (option 2):");
        foreach (var a in addresses) Console.WriteLine($"      {a}");
        if (addresses.Count == 0) Console.WriteLine("      (no network address found)");
        Console.WriteLine("  Players over the internet: forward TCP port 4040 on your router to this PC");
        Console.WriteLine("  and give them your public IP address.");
        Console.WriteLine("  If Windows asks whether to allow network access, choose \"Allow\".");
        Console.WriteLine("  To play yourself on this PC, choose option 1 once - the game will use this server.");
        Console.WriteLine("  Close this window to stop the server.");
        Console.WriteLine();
        int code = await ServerHost.Run(new ServerOptions(ServerHost.DefaultPort, "0.0.0.0", Program.DefaultDataDir, null, Quiet: false));
        if (code != 0) Pause();
        return code;
    }

    static GameDir? AskGameDir()
    {
        Console.WriteLine("  Where is QuiVr installed? In Steam: right-click QuiVr > Manage > Browse local files,");
        Console.WriteLine("  then copy the folder path from the address bar and paste it here.");
        string path = Ask("Game folder", "").Trim('"', ' ');
        if (path.Length > 0 && GameDir.Find(path) is { } game) return game;
        Error("QuiVr was not found in that folder.");
        return null;
    }

    static string Ask(string prompt, string fallback)
    {
        Console.Write(fallback.Length > 0 ? $"  {prompt} [{fallback}]: " : $"  {prompt}: ");
        string? line = Console.ReadLine();
        if (line == null) return "0";
        line = line.Trim();
        if (line.Length == 0) return fallback;
        return prompt.Contains("(y/n)") ? line.ToLowerInvariant() : line;
    }

    public static void Pause()
    {
        Console.WriteLine();
        Console.Write("  Press Enter to close.");
        Console.ReadLine();
    }

    static void Success(string msg) => Colored(ConsoleColor.Green, msg);
    static void Warn(string msg) => Colored(ConsoleColor.Yellow, msg);
    static void Error(string msg) => Colored(ConsoleColor.Red, msg);

    static void Colored(ConsoleColor color, string msg)
    {
        Console.ForegroundColor = color;
        Console.WriteLine($"  {msg}");
        Console.ResetColor();
    }
}
