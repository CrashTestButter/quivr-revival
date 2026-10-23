namespace QuiVrRevival.Installer;

/// <summary>A QuiVr installation and the mod's files inside it.</summary>
public sealed class GameDir(string root)
{
    public string Root { get; } = root;
    public string Managed => Path.Combine(Root, "QuiVr_Data", "Managed");
    public string ModDir => Path.Combine(Root, "QuiVrRevival");
    public string ConfigPath => Path.Combine(ModDir, "config.ini");
    public string ServerExe => Path.Combine(ModDir, "QuiVrRevival.exe");

    /// <summary>
    /// Where the server started by the game keeps its data. On Linux the game, and so the server,
    /// runs under Proton, whose %APPDATA% lives in the game's prefix.
    /// </summary>
    public string LocalProgressDir => OperatingSystem.IsWindows()
        ? Program.DefaultDataDir
        : Path.GetFullPath(Path.Combine(Root, "..", "..", "compatdata", "489380", "pfx", "drive_c", "users", "steamuser", "AppData", "Roaming", "QuiVrRevival"));

    public static bool IsGameDir(string dir) =>
        File.Exists(Path.Combine(dir, "QuiVr_Data", "Managed", "Assembly-CSharp-firstpass.dll"));

    /// <summary>Finds the game from a hint (game folder or any folder inside it) or the Steam libraries.</summary>
    public static GameDir? Find(string? hint = null)
    {
        if (hint != null)
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(hint)); dir != null; dir = dir.Parent)
                if (IsGameDir(dir.FullName)) return new GameDir(dir.FullName);
            return null;
        }
        // Running from <game>/QuiVrRevival/ (the copy the installer puts there)?
        if (Path.GetDirectoryName(Environment.ProcessPath) is { } exeDir && Path.GetDirectoryName(exeDir) is { } parent && IsGameDir(parent))
            return new GameDir(parent);
        foreach (var library in SteamLibraries())
        {
            string candidate = Path.Combine(library, "steamapps", "common", "QuiVr");
            if (IsGameDir(candidate)) return new GameDir(candidate);
        }
        return null;
    }

    static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string steamPath) roots.Add(steamPath.Replace('/', '\\'));
            }
            catch (Exception)
            {
                // Registry not readable; fall back to the usual locations.
            }
            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".local/share/Steam"));
            roots.Add(Path.Combine(home, ".steam/steam"));
            roots.Add(Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            if (seen.Add(Path.GetFullPath(root))) yield return root;
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var line in File.ReadLines(vdf))
            {
                var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => p.Length > 0).ToArray();
                if (parts is ["path", var path])
                {
                    string lib = path.Replace(@"\\", @"\");
                    if (Directory.Exists(lib) && seen.Add(Path.GetFullPath(lib))) yield return lib;
                }
            }
        }
    }

    // ---- Mod configuration (read by the hook inside the game) ----

    public sealed record ModConfig(string ServerUrl, bool AutoStart);

    public ModConfig? ReadConfig()
    {
        if (!File.Exists(ConfigPath)) return null;
        string url = "";
        bool autostart = false;
        foreach (var raw in File.ReadAllLines(ConfigPath))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith('#')) continue;
            string key = line[..eq].Trim().ToLowerInvariant(), value = line[(eq + 1)..].Trim();
            if (key == "server") url = value;
            if (key == "autostart") autostart = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return new ModConfig(url, autostart);
    }

    public void WriteConfig(ModConfig config)
    {
        Directory.CreateDirectory(ModDir);
        File.WriteAllText(ConfigPath,
            "# QuiVr Revival settings, read by the game when it starts.\n" +
            "# server:    where the game saves your progress\n" +
            "# autostart: start QuiVrRevival.exe (in this folder) together with the game\n" +
            $"server={config.ServerUrl}\n" +
            $"autostart={(config.AutoStart ? "true" : "false")}\n");
    }
}
