using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace QuiVrRevival.Server;

/// <summary>
/// The subset of the Parse REST API used by QuiVr's Parse SDK (Unity, 2016). Notable quirks
/// of that SDK: every request is a POST whose JSON body carries the real method in "_method",
/// and query parameters (where, order, limit, login credentials) travel in the URL.
/// </summary>
public sealed class ParseApi(Store store, Catalog catalog, GameConfig config, GameRules rules)
{
    public const string AppId = "QuiVr";

    /// <summary>Log every request (QUIVR_TRACE=1), for debugging.</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("QUIVR_TRACE") == "1";

    static readonly string[] MetaKeys =
    [
        "_method", "_noBody", "_ApplicationId", "_JavaScriptKey", "_ClientVersion", "_InstallationId",
        "_SessionToken", "_RevocableSession", "_ContentType", "_MasterKey",
    ];

    enum Access { Users, Catalog, Open, Owned, Score }

    sealed record Request(string Method, string[] Path, JsonObject? Body, IQueryCollection Query, JsonObject? User, string? Token);

    public async Task Handle(HttpContext ctx, string path)
    {
        int status;
        JsonNode response;
        try
        {
            var body = await ReadBody(ctx.Request);
            string method = ctx.Request.Method.ToUpperInvariant();
            if (ParseJson.Str(body?["_method"]) is { } overridden) method = overridden.ToUpperInvariant();
            bool noBody = body?["_noBody"]?.GetValueKind() == JsonValueKind.True;
            string? appId = Header(ctx, "X-Parse-Application-Id") ?? ParseJson.Str(body?["_ApplicationId"]);
            string? token = Header(ctx, "X-Parse-Session-Token") ?? ParseJson.Str(body?["_SessionToken"]);
            if (body != null)
                foreach (var key in MetaKeys) body.Remove(key);
            if (noBody) body = null;

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["health"])
            {
                await Write(ctx.Response, 200, new JsonObject { ["status"] = "ok" });
                return;
            }
            // The game's "is the server online" check requests the bare URL and expects exactly this.
            if (appId != AppId)
            {
                await Write(ctx.Response, 403, new JsonObject { ["error"] = "unauthorized" });
                return;
            }

            JsonObject? user = null;
            if (!string.IsNullOrEmpty(token))
            {
                user = store.SessionUser(token) is { } uid ? store.Get("_User", uid) : null;
                if (user == null && parts is not ["logout"])
                    throw new ParseError(ParseError.InvalidSessionToken, "Invalid session token");
            }
            (status, response) = Route(new Request(method, parts, body, ctx.Request.Query, user, token));
        }
        catch (ParseError e)
        {
            (status, response) = ErrorResponse(e);
        }
        catch (JsonException)
        {
            (status, response) = ErrorResponse(new ParseError(ParseError.InvalidJson, "Invalid JSON"));
        }
        catch (Exception e)
        {
            Log.Info($"error handling {path}: {e}");
            (status, response) = (500, new JsonObject { ["code"] = 1, ["error"] = "Internal server error." });
        }
        if (Trace)
            Log.Info($"{ctx.Request.Method} /{path}{ctx.Request.QueryString} token={(Header(ctx, "X-Parse-Session-Token") ?? "-")} -> {status} {Truncate(response.ToJsonString(), 300)}");
        await Write(ctx.Response, status, response);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    static (int, JsonNode) ErrorResponse(ParseError e) =>
        (e.Status, new JsonObject { ["code"] = e.Code, ["error"] = e.Message });

    static string? Header(HttpContext ctx, string name) =>
        ctx.Request.Headers.TryGetValue(name, out var v) && v.Count > 0 && !string.IsNullOrEmpty(v[0]) ? v[0] : null;

    static async Task<JsonObject?> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        string text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text) as JsonObject;
    }

    static async Task Write(HttpResponse response, int status, JsonNode body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(body.ToJsonString());
    }

    (int, JsonNode) Route(Request r)
    {
        var p = r.Path;
        switch (p)
        {
            case ["login"]:
                return (200, Login(r));
            case ["logout"]:
                if (r.Token != null)
                    lock (store.WriteLock) store.DeleteSession(r.Token);
                return (200, new JsonObject());
            case ["users"] when r.Method == "POST":
                return (201, SignUp(r.Body ?? new JsonObject()));
            case ["users", "me"]:
            case ["sessions", "me"]:
                var me = r.User ?? throw new ParseError(ParseError.InvalidSessionToken, "Invalid session token");
                return (200, p[0] == "users" ? WithToken(Out(me), r.Token!) : SessionInfo(me, r.Token!));
            case ["users", var userId]:
                return ObjectRoute(r, "_User", userId);
            case ["upgradeToRevocableSession"]:
                return (200, new JsonObject { ["sessionToken"] = r.Token });
            case ["config"]:
                return (200, new JsonObject { ["params"] = config.Params });
            case ["functions", var name]:
                var result = rules.CallFunction(name, r.Body ?? new JsonObject(), r.User);
                return (200, new JsonObject { ["result"] = result });
            case ["classes", var cls] when r.Method == "POST":
                return (201, Create(cls, r.Body ?? new JsonObject(), r.User));
            case ["classes", var cls] when r.Method == "GET":
                return (200, QueryClass(cls, r.Query, r.User));
            case ["classes", var cls, var id]:
                return ObjectRoute(r, cls, id);
            case ["batch"]:
                return (200, Batch(r));
            case ["installations", ..]:
                return (201, new JsonObject { ["objectId"] = ParseJson.NewObjectId(), ["createdAt"] = ParseJson.Now() });
            case ["events", ..]:
            case ["requestPasswordReset"]:
                return (200, new JsonObject());
        }
        throw new ParseError(ParseError.InvalidJson, "Unsupported request", 404);
    }

    (int, JsonNode) ObjectRoute(Request r, string cls, string id) => r.Method switch
    {
        "GET" => (200, GetObject(cls, id, r.User)),
        "PUT" => (200, Update(cls, id, r.Body ?? new JsonObject(), r.User)),
        "DELETE" => (200, Delete(cls, id, r.User)),
        _ => throw new ParseError(ParseError.InvalidJson, "Unsupported method", 405),
    };

    static Access AccessOf(string cls) => cls switch
    {
        "_User" => Access.Users,
        "Items" => Access.Catalog,
        "VersionInfo" or "PlayerMovement" => Access.Open,
        "ArcadeUser" => Access.Owned,
        _ when GameRules.ScoreClasses.Contains(cls) => Access.Score,
        _ => throw ParseError.Forbidden($"This user is not allowed to access non-existent class: {cls}"),
    };

    // ---- Users ----

    JsonObject Login(Request r)
    {
        string username = r.Query["username"].FirstOrDefault() ?? ParseJson.Str(r.Body?["username"]) ?? "";
        string password = r.Query["password"].FirstOrDefault() ?? ParseJson.Str(r.Body?["password"]) ?? "";
        lock (store.WriteLock)
        {
            var user = store.Find("_User", username).FirstOrDefault();
            if (user == null && rules.ShouldAutoCreate(username, password))
            {
                // The game should sign up after a failed first login, but that code never runs.
                user = NewUser(username, password, new JsonObject { ["TimePlayed"] = 10 });
                Log.Info($"new player: {username}");
            }
            if (user == null || !Passwords.Verify(password, ParseJson.Str(user["_hashed_password"])))
                throw new ParseError(ParseError.ObjectNotFound, "Invalid username/password.", 404);

            var changed = new JsonObject();
            rules.FixUser(user, changed);
            if (changed.Count > 0) store.Put("_User", user);
            string token = store.CreateSession(ParseJson.Str(user["objectId"])!);
            Log.Info($"login: {ParseJson.Str(user["name"])} ({username})");
            return WithToken(Out(user), token);
        }
    }

    JsonObject SignUp(JsonObject body)
    {
        string username = ParseJson.Str(body["username"]) ?? throw new ParseError(ParseError.UsernameMissing, "bad or missing username");
        string password = ParseJson.Str(body["password"]) ?? throw new ParseError(ParseError.PasswordMissing, "password is required");
        lock (store.WriteLock)
        {
            if (store.Find("_User", username).Any())
                throw new ParseError(ParseError.UsernameTaken, "Account already exists for this username.");
            var user = NewUser(username, password, body);
            string token = store.CreateSession(ParseJson.Str(user["objectId"])!);
            return new JsonObject { ["objectId"] = user["objectId"]!.DeepClone(), ["createdAt"] = user["createdAt"]!.DeepClone(), ["sessionToken"] = token };
        }
    }

    JsonObject NewUser(string username, string password, JsonObject fields)
    {
        string now = ParseJson.Now();
        var user = new JsonObject
        {
            ["objectId"] = ParseJson.NewObjectId(),
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };
        var extra = (JsonObject)fields.DeepClone();
        extra.Remove("password");
        ParseJson.Apply(user, extra, new JsonObject());
        user["username"] = username;
        user["_hashed_password"] = Passwords.Hash(password);
        rules.FixUser(user, new JsonObject());
        store.Put("_User", user);
        return user;
    }

    static JsonObject WithToken(JsonObject user, string token)
    {
        user["sessionToken"] = token;
        return user;
    }

    static JsonObject SessionInfo(JsonObject user, string token) => new()
    {
        ["sessionToken"] = token,
        ["user"] = new JsonObject { ["__type"] = "Pointer", ["className"] = "_User", ["objectId"] = ParseJson.Str(user["objectId"]) },
    };

    // ---- Objects ----

    /// <summary>Copy for the client: internal fields (starting with "_") removed.</summary>
    static JsonObject Out(JsonObject obj)
    {
        var copy = (JsonObject)obj.DeepClone();
        foreach (var key in copy.Select(kv => kv.Key).Where(k => k.StartsWith('_')).ToList()) copy.Remove(key);
        return copy;
    }

    static string? IdOf(JsonObject? obj) => obj == null ? null : ParseJson.Str(obj["objectId"]);

    JsonObject GetObject(string cls, string id, JsonObject? user)
    {
        switch (AccessOf(cls))
        {
            case Access.Users:
                return IdOf(user) == id ? Out(user!) : throw ParseError.NotFound();
            case Access.Catalog:
                return Out(catalog.Items.FirstOrDefault(i => IdOf(i) == id) ?? throw ParseError.NotFound());
            default:
                return Out(store.Get(cls, id) ?? throw ParseError.NotFound());
        }
    }

    JsonObject Create(string cls, JsonObject body, JsonObject? user)
    {
        var access = AccessOf(cls);
        if (access == Access.Users) return SignUp(body);
        if (access == Access.Catalog) throw ParseError.Forbidden();
        if (access is Access.Owned or Access.Score && user == null) throw ParseError.Forbidden("Not logged in");

        string now = ParseJson.Now();
        var obj = new JsonObject { ["objectId"] = ParseJson.NewObjectId(), ["createdAt"] = now, ["updatedAt"] = now };
        var changed = new JsonObject();
        ParseJson.Apply(obj, body, changed);
        if (user != null) obj["_owner"] = IdOf(user);
        if (access == Access.Score) rules.ValidateScore(cls, obj, user!);
        lock (store.WriteLock) store.Put(cls, obj);
        changed["objectId"] = obj["objectId"]!.DeepClone();
        changed["createdAt"] = now;
        return changed;
    }

    JsonObject Update(string cls, string id, JsonObject body, JsonObject? user)
    {
        var access = AccessOf(cls);
        if (access == Access.Catalog) throw ParseError.Forbidden();
        lock (store.WriteLock)
        {
            var obj = store.Get(cls, id) ?? throw ParseError.NotFound();
            if (access == Access.Users && IdOf(user) != id) throw ParseError.NotFound();
            if (access is Access.Owned or Access.Score) CheckOwner(obj, user);

            var changed = new JsonObject();
            var fields = (JsonObject)body.DeepClone();
            if (access == Access.Users)
            {
                // Account identity is fixed; the game never changes it.
                fields.Remove("username");
                fields.Remove("password");
            }
            ParseJson.Apply(obj, fields, changed);
            if (access == Access.Users) rules.FixUser(obj, changed);
            if (access == Access.Score) rules.ValidateScore(cls, obj, user!);
            string now = ParseJson.Now();
            obj["updatedAt"] = now;
            store.Put(cls, obj);
            changed["updatedAt"] = now;
            return changed;
        }
    }

    JsonObject Delete(string cls, string id, JsonObject? user)
    {
        if (AccessOf(cls) != Access.Owned) throw ParseError.Forbidden();
        lock (store.WriteLock)
        {
            var obj = store.Get(cls, id) ?? throw ParseError.NotFound();
            CheckOwner(obj, user);
            store.Delete(cls, id);
        }
        return new JsonObject();
    }

    static void CheckOwner(JsonObject obj, JsonObject? user)
    {
        string? owner = ParseJson.Str(obj["_owner"]);
        if (owner != null && owner != IdOf(user)) throw ParseError.Forbidden();
    }

    JsonObject QueryClass(string cls, IQueryCollection query, JsonObject? user)
    {
        var access = AccessOf(cls);
        if (access == Access.Users) throw ParseError.Forbidden();

        JsonObject? where = query["where"].FirstOrDefault() is { Length: > 0 } w
            ? JsonNode.Parse(w) as JsonObject ?? throw new ParseError(ParseError.InvalidQuery, "where must be an object")
            : null;

        IEnumerable<JsonObject> source;
        if (access == Access.Catalog)
        {
            source = catalog.Items;
        }
        else
        {
            string? lookup = Store.LookupFieldOf(cls) is { } field && where != null ? ParseJson.Str(where[field]) : null;
            source = store.Find(cls, lookup);
        }

        var matches = ParseJson.Order(source.Where(o => ParseJson.Matches(o, where)), query["order"].FirstOrDefault()).ToList();
        int skip = int.TryParse(query["skip"].FirstOrDefault(), out var s) ? Math.Max(0, s) : 0;
        int limit = int.TryParse(query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 0, 1000) : 100;
        var results = new JsonArray(matches.Skip(skip).Take(limit).Select(o => (JsonNode)Out(o)).ToArray());
        var response = new JsonObject { ["results"] = results };
        if (query["count"].FirstOrDefault() is "1" or "true") response["count"] = matches.Count;
        return response;
    }

    JsonArray Batch(Request r)
    {
        var responses = new JsonArray();
        foreach (var item in (r.Body?["requests"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            try
            {
                string path = ParseJson.Str(item["path"]) ?? "";
                // Paths look like "/1/classes/X" or "/parse/classes/X"; keep what follows the mount.
                var segments = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
                int start = segments.FindIndex(x => x is "classes" or "users" or "functions" or "login" or "logout" or "config");
                var sub = new Request(
                    (ParseJson.Str(item["method"]) ?? "GET").ToUpperInvariant(),
                    start < 0 ? [] : segments.Skip(start).ToArray(),
                    item["body"]?.DeepClone() as JsonObject,
                    r.Query, r.User, r.Token);
                if (sub.Path is ["batch"]) throw new ParseError(ParseError.InvalidJson, "nested batch");
                responses.Add(new JsonObject { ["success"] = Route(sub).Item2 });
            }
            catch (ParseError e)
            {
                responses.Add(new JsonObject { ["error"] = new JsonObject { ["code"] = e.Code, ["error"] = e.Message } });
            }
        }
        return responses;
    }
}
