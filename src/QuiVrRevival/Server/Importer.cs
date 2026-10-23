using System.Text.Json.Nodes;

namespace QuiVrRevival.Server;

/// <summary>
/// Imports data from a Parse Server / MongoDB setup (the project's earlier Docker version).
/// Input: {"_User":[...], "FFKDiff":[...], ...} with documents as stored in MongoDB
/// (_id, _created_at, _updated_at, ...), e.g. exported with mongosh.
/// </summary>
public static class Importer
{
    static readonly string[] Classes = ["_User", "ArcadeUser", "VersionInfo", "PlayerMovement", .. GameRules.ScoreClasses];

    public static int Run(string file, string dataDir)
    {
        var input = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        using var store = new Store(Path.Combine(dataDir, "quivr.db"));
        var usersByName = new Dictionary<string, string>();
        int total = 0;
        foreach (var cls in Classes)
        {
            if (input[cls] is not JsonArray docs) continue;
            foreach (var doc in docs.OfType<JsonObject>())
            {
                var obj = new JsonObject();
                foreach (var (key, value) in doc)
                {
                    switch (key)
                    {
                        case "_id": obj["objectId"] = value?.DeepClone(); break;
                        case "_created_at": obj["createdAt"] = Date(value); break;
                        case "_updated_at": obj["updatedAt"] = Date(value); break;
                        default:
                            if (key.StartsWith('_')) break;
                            obj[key] = Date(value) is { } d && value is JsonObject { Count: 1 } o && o.ContainsKey("$date")
                                ? new JsonObject { ["__type"] = "Date", ["iso"] = d }
                                : value?.DeepClone();
                            break;
                    }
                }
                if (ParseJson.Str(obj["objectId"]) == null) continue;
                if (cls == "_User")
                {
                    string username = ParseJson.Str(obj["username"]) ?? "";
                    // Game accounts use the platform user ID as password.
                    obj["_hashed_password"] = Passwords.Hash(username);
                    usersByName[username] = ParseJson.Str(obj["objectId"])!;
                }
                else if (ParseJson.Str(obj["UserID"]) is { } owner && usersByName.TryGetValue(owner.Split('_')[0], out var ownerId))
                {
                    obj["_owner"] = ownerId;
                }
                else if (doc["_p_Owner"] is JsonValue p && ParseJson.Str(p)?.Split('$') is [_, var id])
                {
                    obj["_owner"] = id;
                }
                store.Put(cls, obj);
                total++;
            }
            Console.WriteLine($"{cls}: {docs.Count}");
        }
        Console.WriteLine($"Imported {total} objects into {dataDir}");
        return 0;
    }

    static string? Date(JsonNode? value) => value switch
    {
        JsonObject o when o["$date"] is { } d => ParseJson.Str(d) is { } s
            ? ParseJson.FormatDate(DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal))
            : null,
        JsonValue v when ParseJson.Str(v) is { } s => s,
        _ => null,
    };
}
