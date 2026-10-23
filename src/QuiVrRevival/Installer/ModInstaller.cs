using QuiVrRevival.Server;

namespace QuiVrRevival.Installer;

public static class ModInstaller
{
    public static string LocalUrl(int port) => $"http://127.0.0.1:{port}/parse/";

    /// <summary>
    /// Play on this computer: the game starts its own server. Needs the Windows exe in the mod
    /// folder (the game runs it, natively or under Proton) - this program itself on Windows,
    /// or a QuiVrRevival.exe placed next to the Linux build.
    /// </summary>
    public static bool InstallLocal(GameDir game, int port = ServerHost.DefaultPort)
    {
        GamePatcher.Patch(game);
        Directory.CreateDirectory(game.ModDir);
        string? exe = WindowsExe();
        // Written rather than copied, so Windows' "downloaded from the internet" mark is not carried over.
        if (exe != null && !SamePath(exe, game.ServerExe))
            File.WriteAllBytes(game.ServerExe, File.ReadAllBytes(exe));
        bool autostart = File.Exists(game.ServerExe);
        game.WriteConfig(new GameDir.ModConfig(LocalUrl(port), autostart));
        return autostart;
    }

    /// <summary>Play on someone else's server.</summary>
    public static void InstallRemote(GameDir game, string url)
    {
        GamePatcher.Patch(game);
        game.WriteConfig(new GameDir.ModConfig(url, AutoStart: false));
    }

    public static void Uninstall(GameDir game)
    {
        GamePatcher.Restore(game);
        if (!Directory.Exists(game.ModDir)) return;
        // The running copy of this program may be the one in the mod folder; leave it if so.
        foreach (var file in Directory.GetFiles(game.ModDir))
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(game.ModDir); } catch (IOException) { }
    }

    static string? WindowsExe()
    {
        string? self = Environment.ProcessPath;
        if (OperatingSystem.IsWindows()) return self;
        string? sibling = self == null ? null : Path.Combine(Path.GetDirectoryName(self)!, "QuiVrRevival.exe");
        return sibling != null && File.Exists(sibling) ? sibling : null;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Turns "1.2.3.4", "host:4040" or a full URL into the URL the game needs.</summary>
    public static string? NormalizeServerAddress(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;
        if (!input.Contains("://")) input = "http://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0)
            return null;
        var builder = new UriBuilder(uri);
        if (uri.IsDefaultPort && !input.Contains($":{uri.Port}") && uri.Scheme == "http") builder.Port = ServerHost.DefaultPort;
        if (builder.Path is "" or "/") builder.Path = "/parse/";
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri.ToString();
    }

    /// <summary>True if a QuiVr server answers at the URL (it rejects requests without the app ID).</summary>
    public static async Task<bool> CheckServer(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await http.GetAsync(url);
            string body = await response.Content.ReadAsStringAsync();
            return body.Contains("unauthorized");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
