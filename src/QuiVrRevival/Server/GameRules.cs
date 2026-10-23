using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QuiVrRevival.Server;

/// <summary>
/// QuiVr-specific server behaviour: the cloud functions the client calls (Armory, ItemGenerator,
/// SteamSetup), leaderboard ownership, and workarounds for client bugs.
///
/// Inventories are stored on the user (or ArcadeUser) as one string, "item$item$item", where each
/// item is ArmorOption.ToString(): Name%ItemType%ObjectID%r,g,b,a:...%Rarity%Ancient|EffectID^Value
/// </summary>
public sealed partial class GameRules(Store store, Catalog catalog)
{
    public static readonly string[] ScoreClasses = ["FFKDiff", "CanyonMPDiff", "ArcheryRange01", "OwlInvaders"];

    const char Sep = '$';

    [GeneratedRegex(@"^[\w.\-]{5,64}$")]
    private static partial Regex PlatformId();

    /// <summary>The game logs in with its platform user ID (e.g. the SteamID) as both username and password.</summary>
    public bool ShouldAutoCreate(string username, string password) => username == password && PlatformId().IsMatch(username);

    /// <summary>Keeps user objects in a shape the client can read.</summary>
    public void FixUser(JsonObject user, JsonObject changed)
    {
        // Read unchecked right after login; the game then replaces it with the player's name.
        if (user["name"] == null)
        {
            user["name"] = "Archer";
            changed["name"] = "Archer";
        }
        // GetRatity() creates NxtLgdr with ParseObject.Add (an array) but reads it with Get<int>.
        if (user["NxtLgdr"] is JsonArray arr)
        {
            double n = arr.Count > 0 ? ParseJson.ToDouble(arr[^1]) ?? 50 : 50;
            user["NxtLgdr"] = ParseJson.NumberNode(n);
            changed["NxtLgdr"] = ParseJson.NumberNode(n);
        }
    }

    /// <summary>Players may only post scores under their own account (or their arcade profiles).</summary>
    public void ValidateScore(string cls, JsonObject score, JsonObject user)
    {
        string name = ParseJson.Str(user["username"]) ?? "";
        string id = ParseJson.Str(score["UserID"]) ?? "";
        if (id != name && !id.StartsWith(name + "_", StringComparison.Ordinal))
            throw ParseError.Forbidden("Not your score");
        if (ParseJson.Str(score["Username"]) is { } display && ParseJson.ToDouble(score["Score"]) is { } value)
            Log.Info($"score: {display} {value} on {cls}");
    }

    public string CallFunction(string name, JsonObject p, JsonObject? user) => name switch
    {
        "AddItem" => AddItem(p, Require(user)),
        "ReplaceItemV3" => ReplaceItem(p, Require(user)),
        "GetItemV2" => GetItem(p, user),
        "FixInventories" => user == null ? "0" : FixInventories(user),
        _ => throw new ParseError(ParseError.ScriptFailed, "Invalid function."),
    };

    static JsonObject Require(JsonObject? user) =>
        user ?? throw new ParseError(ParseError.InvalidSessionToken, "Not logged in");

    static List<string> Split(string? inventory) =>
        string.IsNullOrEmpty(inventory) || inventory.Length < 2
            ? []
            : inventory.Split(Sep).Where(i => i.Length > 5).ToList();

    // "Name%ItemType" identifies an item independent of its rolled stats.
    static string ItemKey(string item) => string.Join('%', item.Split('|')[0].Split('%').Take(2));

    static bool IsValidItem(string item)
    {
        var halves = item.Split('|');
        if (halves.Length != 2 || !Regex.IsMatch(halves[1], @"^\d+\^")) return false;
        var parts = halves[0].Split('%');
        return parts.Length >= 4 && parts[0].Length > 0 && parts[0] != "Undefined";
    }

    /// <summary>Loads the object holding the inventory and checks the caller owns it.</summary>
    (string cls, JsonObject obj) Owner(JsonObject p, JsonObject user)
    {
        string id = ParseJson.Str(p["userid"]) ?? "";
        string userId = ParseJson.Str(user["objectId"])!;
        if (p["arcade"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
        {
            var arcade = store.Get("ArcadeUser", id) ?? throw ParseError.NotFound();
            string? owner = ParseJson.Str(arcade["_owner"]) ?? ParseJson.Str(arcade["Owner"]?["objectId"]);
            if (owner != userId) throw ParseError.Forbidden("Not your arcade user");
            return ("ArcadeUser", arcade);
        }
        if (id != userId) throw ParseError.Forbidden("Not your user");
        return ("_User", store.Get("_User", id) ?? throw ParseError.NotFound());
    }

    void Save(string cls, JsonObject obj, List<string> inventory)
    {
        obj["Inventory"] = string.Join(Sep, inventory);
        obj["updatedAt"] = ParseJson.Now();
        store.Put(cls, obj);
    }

    // Armory.AddItemSync: one item, or a "$"-joined list (the starter set).
    string AddItem(JsonObject p, JsonObject user)
    {
        lock (store.WriteLock)
        {
            var (cls, obj) = Owner(p, user);
            var inventory = Split(ParseJson.Str(obj["Inventory"]));
            int added = 0;
            foreach (var item in Split(ParseJson.Str(p["item"])))
            {
                if (inventory.Contains(item)) continue;
                inventory.Add(item);
                added++;
            }
            Save(cls, obj, inventory);
            return $"added {added}";
        }
    }

    // Armory.ReplaceItemSync: reforge (old -> new) or disenchant (new is empty).
    string ReplaceItem(JsonObject p, JsonObject user)
    {
        lock (store.WriteLock)
        {
            var (cls, obj) = Owner(p, user);
            var inventory = Split(ParseJson.Str(obj["Inventory"]));
            string oldItem = ParseJson.Str(p["olditem"]) ?? "";
            string newItem = ParseJson.Str(p["newitem"]) ?? "";
            int idx = inventory.IndexOf(oldItem);
            if (idx < 0 && oldItem.Length > 0) idx = inventory.FindIndex(i => ItemKey(i) == ItemKey(oldItem));
            if (idx >= 0)
            {
                if (newItem.Length > 0) inventory[idx] = newItem;
                else inventory.RemoveAt(idx);
            }
            else if (newItem.Length > 0)
            {
                inventory.Add(newItem);
            }
            Save(cls, obj, inventory);
            return idx >= 0 ? "replaced" : "appended";
        }
    }

    // ItemGenerator.GetRandomItem: a catalog item of the requested rarity, preferring items the
    // player doesn't own and the slot they have the fewest items in. The client saves it via AddItem.
    string GetItem(JsonObject p, JsonObject? user)
    {
        double rarity = ParseJson.ToDouble(p["rarity"]) ?? -1;
        double wantedType = ParseJson.ToDouble(p["itemtype"]) ?? -1;
        var owned = (p["owned"] as JsonArray)?.Select(ParseJson.Str).Where(s => s != null).ToHashSet() ?? [];

        var all = catalog.Items.Where(i => ParseJson.ToDouble(i["Rarity"]) == rarity).ToList();
        if (all.Count == 0) return "";
        var notOwned = all.Where(i => !owned.Contains(ParseJson.Str(i["Name"]))).ToList();
        var pool = notOwned.Count > 0 ? notOwned : all;
        var wanted = pool.Where(i => ParseJson.ToDouble(i["ItemType"]) == wantedType).ToList();
        var choices = wanted.Count > 0 ? wanted : pool;
        var pick = choices[Random.Shared.Next(choices.Count)];

        Log.Info($"item: {(user == null ? "?" : ParseJson.Str(user["name"]))} received {ParseJson.Str(pick["Name"])}");
        // Same shape the original server returned (see ItemGenerator.cs in the PSVR source).
        return new JsonObject
        {
            ["Rarity"] = pick["Rarity"]?.DeepClone(),
            ["Name"] = pick["Name"]?.DeepClone(),
            ["EffectID"] = pick["EffectID"]?.DeepClone(),
            ["ItemType"] = pick["ItemType"]?.DeepClone(),
            ["Meshes"] = (ParseJson.ToDouble(pick["Meshes"]) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Colors"] = ParseJson.Str(pick["Colors"]) ?? "",
        }.ToJsonString();
    }

    // SteamSetup.QueryVersion calls this on every start: drop broken and duplicate entries.
    string FixInventories(JsonObject user)
    {
        lock (store.WriteLock)
        {
            var obj = store.Get("_User", ParseJson.Str(user["objectId"])!) ?? throw ParseError.NotFound();
            var before = Split(ParseJson.Str(obj["Inventory"]));
            var after = before.Where(IsValidItem).Distinct().ToList();
            if (after.Count != before.Count) Save("_User", obj, after);
            return (before.Count - after.Count).ToString();
        }
    }
}
