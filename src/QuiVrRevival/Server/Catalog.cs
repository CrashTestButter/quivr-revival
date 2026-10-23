using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace QuiVrRevival.Server;

/// <summary>
/// The loot table (the game's "Items" class). Read-only; comes from the embedded items.json,
/// or from items.json in the data folder if a host wants to change it.
/// </summary>
public sealed class Catalog
{
    public IReadOnlyList<JsonObject> Items { get; }

    public Catalog(string dataDir)
    {
        string custom = Path.Combine(dataDir, "items.json");
        string json = File.Exists(custom) ? File.ReadAllText(custom) : ReadResource("items.json");
        var items = new List<JsonObject>();
        foreach (var node in JsonNode.Parse(json)!.AsArray())
        {
            var item = (JsonObject)node!.DeepClone();
            string name = ParseJson.Str(item["Name"]) ?? "";
            item["objectId"] = StableId(name);
            item["createdAt"] = "2017-01-01T00:00:00.000Z";
            item["updatedAt"] = "2017-01-01T00:00:00.000Z";
            items.Add(item);
        }
        Items = items;
    }

    static string StableId(string name)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return new string(hash.Take(10).Select(b => chars[b % chars.Length]).ToArray());
    }

    public static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
