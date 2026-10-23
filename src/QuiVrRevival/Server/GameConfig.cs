using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuiVrRevival.Server;

/// <summary>
/// Values the game reads through ParseConfig. Stored in game-config.json in the data folder,
/// re-read when the file changes so a host can edit it while the server runs.
/// </summary>
public sealed class GameConfig
{
    static readonly JsonObject Defaults = new()
    {
        // Shown to players in the lobby when longer than 5 characters.
        ["ServerMessage"] = "",
        ["BetaMessage"] = "",
        ["WeeklySeed"] = "0",
        ["RespawnTime"] = 3,
        ["InvincibleTime"] = 5,
        ["DiffMult"] = 1,
    };

    readonly string path;
    JsonObject current = new();
    DateTime loadedStamp;

    public GameConfig(string dataDir)
    {
        path = Path.Combine(dataDir, "game-config.json");
        if (!File.Exists(path))
            File.WriteAllText(path, Defaults.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public JsonObject Params
    {
        get
        {
            lock (this)
                return Load();
        }
    }

    JsonObject Load()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp != loadedStamp)
            {
                var loaded = (JsonObject)Defaults.DeepClone();
                foreach (var (k, v) in JsonNode.Parse(File.ReadAllText(path))!.AsObject())
                    loaded[k] = v?.DeepClone();
                current = loaded;
                loadedStamp = stamp;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            // Keep the last good values while the file is being edited.
        }
        return (JsonObject)current.DeepClone();
    }
}
