using System.Text.Json.Nodes;
using LiteDB;

namespace QuiVrRevival.Server;

/// <summary>
/// Persists Parse objects in a single LiteDB file. Each class is a collection of
/// { _id: objectId, json: "...", k: lookup key }, where the lookup key is the field
/// the game filters that class by (username, UserID, Name, ...).
/// </summary>
public sealed class Store : IDisposable
{
    readonly LiteDatabase db;

    /// <summary>All writes go through this lock so read-modify-write sequences are atomic.</summary>
    public object WriteLock { get; } = new();

    static readonly Dictionary<string, string> LookupField = new()
    {
        ["_User"] = "username",
        ["ArcadeUser"] = "Name",
        ["VersionInfo"] = "Version",
        ["FFKDiff"] = "UserID",
        ["CanyonMPDiff"] = "UserID",
        ["ArcheryRange01"] = "UserID",
        ["OwlInvaders"] = "UserID",
    };

    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
    }

    static string Collection(string className) => "c" + className;

    ILiteCollection<BsonDocument> Col(string className)
    {
        var col = db.GetCollection(Collection(className));
        col.EnsureIndex("k");
        return col;
    }

    public static string? LookupFieldOf(string className) => LookupField.GetValueOrDefault(className);

    public JsonObject? Get(string className, string objectId)
    {
        var doc = Col(className).FindById(objectId);
        return doc == null ? null : (JsonObject)JsonNode.Parse(doc["json"].AsString)!;
    }

    public IEnumerable<JsonObject> Find(string className, string? lookupValue = null)
    {
        var col = Col(className);
        var docs = lookupValue == null ? col.FindAll() : col.Find(Query.EQ("k", lookupValue));
        return docs.Select(d => (JsonObject)JsonNode.Parse(d["json"].AsString)!).ToList();
    }

    public void Put(string className, JsonObject obj)
    {
        string id = ParseJson.Str(obj["objectId"]) ?? throw new InvalidOperationException("objectId missing");
        var doc = new BsonDocument
        {
            ["_id"] = id,
            ["json"] = obj.ToJsonString(),
            ["k"] = LookupFieldOf(className) is { } f && ParseJson.Str(obj[f]) is { } k ? k : BsonValue.Null,
        };
        Col(className).Upsert(doc);
    }

    public bool Delete(string className, string objectId) => Col(className).Delete(objectId);

    public int Count(string className) => Col(className).Count();

    // ---- Sessions ----

    public string CreateSession(string userId)
    {
        string token = ParseJson.NewSessionToken();
        var sessions = db.GetCollection("sessions");
        sessions.EnsureIndex("user");
        // Every game start logs in again; keep only the newest few sessions per player.
        foreach (var old in sessions.Find(Query.EQ("user", userId)).OrderByDescending(s => s["created"].AsDateTime).Skip(4).ToList())
            sessions.Delete(old["_id"]);
        sessions.Insert(new BsonDocument
        {
            ["_id"] = token,
            ["user"] = userId,
            ["created"] = DateTime.UtcNow,
        });
        return token;
    }

    public string? SessionUser(string token) => db.GetCollection("sessions").FindById(token)?["user"].AsString;

    public void DeleteSession(string token) => db.GetCollection("sessions").Delete(token);

    public void Dispose() => db.Dispose();
}
