using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuiVrRevival.Server;

/// <summary>The parts of Parse's REST data model the game uses: field operations, queries, dates.</summary>
public static class ParseJson
{
    const string IdChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string NewObjectId() => RandomNumberGenerator.GetString(IdChars, 10);

    public static string NewSessionToken() => "r:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Now() => FormatDate(DateTime.UtcNow);

    public static string FormatDate(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Field names the game may not write directly.</summary>
    public static bool IsReserved(string key) =>
        key is "objectId" or "createdAt" or "updatedAt" or "sessionToken" or "authData" || key.StartsWith('_');

    /// <summary>
    /// Applies a create/update body to <paramref name="target"/>. Values produced by operations
    /// (e.g. the new value of an Increment) are copied to <paramref name="changed"/>, which is
    /// what Parse returns to the client.
    /// </summary>
    public static void Apply(JsonObject target, JsonObject body, JsonObject changed)
    {
        foreach (var (key, value) in body)
        {
            if (IsReserved(key)) continue;
            if (value is JsonObject op && op["__op"] is JsonValue opName)
            {
                switch (opName.GetValue<string>())
                {
                    case "Delete":
                        target.Remove(key);
                        break;
                    case "Increment":
                        double amount = ToDouble(op["amount"]) ?? 1;
                        double current = ToDouble(target[key]) ?? 0;
                        target[key] = NumberNode(current + amount);
                        changed[key] = target[key]!.DeepClone();
                        break;
                    case "Add":
                    case "AddUnique":
                    case "Remove":
                        var existing = target[key] as JsonArray;
                        var list = existing ?? new JsonArray();
                        var objects = op["objects"] as JsonArray ?? new JsonArray();
                        var name = opName.GetValue<string>();
                        foreach (var o in objects)
                        {
                            if (name == "Add") list.Add(o?.DeepClone());
                            else if (name == "AddUnique" && !list.Any(e => JsonEquals(e, o))) list.Add(o?.DeepClone());
                            else if (name == "Remove")
                                foreach (var e in list.Where(e => JsonEquals(e, o)).ToList()) list.Remove(e);
                        }
                        if (existing == null) target[key] = list;
                        changed[key] = target[key]!.DeepClone();
                        break;
                    default:
                        throw new ParseError(ParseError.InvalidJson, $"Unsupported operation {opName}");
                }
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    public static JsonNode NumberNode(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);

    public static double? ToDouble(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    static bool? Bool(JsonNode? n) => n?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (PointerId(a) is { } pa && PointerId(b) is { } pb) return pa == pb;
        var ca = Comparable(a);
        var cb = Comparable(b);
        if (ca != null && cb != null) return ca.GetType() == cb.GetType() && Compare(ca, cb) == 0;
        return JsonNode.DeepEquals(a, b);
    }

    static string? PointerId(JsonNode n) =>
        n is JsonObject o && Str(o["__type"]) == "Pointer" ? Str(o["objectId"]) : null;

    // ---- Queries ----

    public static bool Matches(JsonObject obj, JsonObject? where)
    {
        if (where == null) return true;
        foreach (var (key, cond) in where)
        {
            if (key == "$or")
            {
                if (cond is not JsonArray ors || !ors.OfType<JsonObject>().Any(o => Matches(obj, o))) return false;
                continue;
            }
            if (key == "$and")
            {
                if (cond is not JsonArray ands || !ands.OfType<JsonObject>().All(o => Matches(obj, o))) return false;
                continue;
            }
            var field = obj[key];
            if (cond is JsonObject c && c.Any(kv => kv.Key.StartsWith('$')))
            {
                foreach (var (op, arg) in c)
                    if (!MatchOperator(field, op, arg)) return false;
            }
            else if (!EqualsOrContains(field, cond))
            {
                return false;
            }
        }
        return true;
    }

    static bool EqualsOrContains(JsonNode? field, JsonNode? value) =>
        field is JsonArray arr && value is not JsonArray ? arr.Any(e => JsonEquals(e, value)) : JsonEquals(field, value);

    static bool MatchOperator(JsonNode? field, string op, JsonNode? arg)
    {
        switch (op)
        {
            case "$exists":
                bool want = Bool(arg) == true;
                return (field != null) == want;
            case "$ne":
                return !EqualsOrContains(field, arg);
            case "$in":
                return arg is JsonArray inList && inList.Any(a => EqualsOrContains(field, a));
            case "$nin":
                return arg is not JsonArray ninList || !ninList.Any(a => EqualsOrContains(field, a));
            case "$all":
                return arg is JsonArray all && field is JsonArray have && all.All(a => have.Any(h => JsonEquals(h, a)));
            case "$gt":
            case "$gte":
            case "$lt":
            case "$lte":
                if (field == null || arg == null) return false;
                var cf = Comparable(field, Comparable(arg) is DateTime);
                var ca = Comparable(arg);
                if (cf == null || ca == null) return false;
                int cmp;
                try { cmp = Compare(cf, ca); } catch (ArgumentException) { return false; }
                return op switch { "$gt" => cmp > 0, "$gte" => cmp >= 0, "$lt" => cmp < 0, _ => cmp <= 0 };
            default:
                throw new ParseError(ParseError.InvalidQuery, $"Unsupported query operator {op}");
        }
    }

    /// <summary>Numbers as double, dates as DateTime, strings and bools as-is.</summary>
    public static object? Comparable(JsonNode? n, bool stringAsDate = false)
    {
        switch (n)
        {
            case JsonObject o when Str(o["__type"]) == "Date":
                return ParseDate(Str(o["iso"]));
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                return ToDouble(v);
            case JsonValue v when Bool(v) is { } b:
                return b;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return stringAsDate ? ParseDate(v.GetValue<string>()) : v.GetValue<string>();
            default:
                return null;
        }
    }

    static object? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;

    static int Compare(object a, object b) => (a, b) switch
    {
        (double x, double y) => x.CompareTo(y),
        (DateTime x, DateTime y) => x.CompareTo(y),
        (string x, string y) => string.CompareOrdinal(x, y),
        (bool x, bool y) => x.CompareTo(y),
        _ => throw new ArgumentException("incomparable"),
    };

    public static IEnumerable<JsonObject> Order(IEnumerable<JsonObject> items, string? order)
    {
        if (string.IsNullOrWhiteSpace(order)) return items;
        IOrderedEnumerable<JsonObject>? sorted = null;
        foreach (var raw in order.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool desc = raw.StartsWith('-');
            string key = desc ? raw[1..] : raw;
            bool asDate = key is "createdAt" or "updatedAt";
            Func<JsonObject, object?> sel = o => Comparable(o[key], asDate);
            var comparer = Comparer<object?>.Create(CompareNullable);
            sorted = sorted == null
                ? (desc ? items.OrderByDescending(sel, comparer) : items.OrderBy(sel, comparer))
                : (desc ? sorted.ThenByDescending(sel, comparer) : sorted.ThenBy(sel, comparer));
        }
        return sorted ?? items;
    }

    static int CompareNullable(object? a, object? b)
    {
        if (a == null || b == null) return a == null ? (b == null ? 0 : -1) : 1;
        try { return Compare(a, b); }
        catch (ArgumentException) { return string.CompareOrdinal(a.GetType().Name, b.GetType().Name); }
    }
}
