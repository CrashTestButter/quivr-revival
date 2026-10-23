// Points an installed copy of QuiVr at a replacement server.
//
// The original server address is compiled into two places:
//   Assembly-CSharp-firstpass.dll  ParseInitBehavior.Awake    (Parse API URL, string literal)
//   Assembly-CSharp.dll            SteamSetup.checkServerOnline (reads AppValues.AppServer, set in a scene asset)
// The first is rewritten in place; for the second, the field read is replaced by the new URL.
// Originals are kept as *.orig and every run patches from them, so it can be re-run with another URL.

using Mono.Cecil;
using Mono.Cecil.Cil;

const string OriginalUrl = "http://34.198.149.161:4040/parse/";
const string DefaultUrl = "http://127.0.0.1:4040/parse/";
string[] Targets = { "Assembly-CSharp-firstpass.dll", "Assembly-CSharp.dll" };

string? gameDir = null;
string url = DefaultUrl;
bool restore = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--url" when i + 1 < args.Length:
            url = args[++i];
            break;
        case "--restore":
            restore = true;
            break;
        case "-h":
        case "--help":
            Console.WriteLine("Usage: QuiVrPatcher [game folder] [--url http://host:port/parse/] [--restore]");
            Console.WriteLine($"Default URL: {DefaultUrl}");
            return 0;
        default:
            gameDir = args[i];
            break;
    }
}

if (!url.EndsWith('/')) url += "/";
if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
{
    Console.Error.WriteLine($"Not a valid http(s) URL: {url}");
    return 1;
}

string? managed = FindManaged(gameDir);
if (managed == null)
{
    Console.Error.WriteLine("Could not find the QuiVr install. Pass the game folder, e.g.");
    Console.Error.WriteLine("  QuiVrPatcher \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\QuiVr\"");
    return 1;
}
Console.WriteLine($"Game assemblies: {managed}");

if (restore)
{
    foreach (var name in Targets)
    {
        var dll = Path.Combine(managed, name);
        if (File.Exists(dll + ".orig"))
        {
            File.Copy(dll + ".orig", dll, overwrite: true);
            File.Delete(dll + ".orig");
            Console.WriteLine($"Restored {name}");
        }
    }
    return 0;
}

foreach (var name in Targets)
{
    var dll = Path.Combine(managed, name);
    if (!File.Exists(dll + ".orig")) File.Copy(dll, dll + ".orig");
}

try
{
    int a = PatchModule(managed, "Assembly-CSharp-firstpass.dll", ReplaceUrlLiteral);
    int b = PatchModule(managed, "Assembly-CSharp.dll", ReplaceAppServerRead);
    if (a == 0 || b == 0)
    {
        Console.Error.WriteLine("Expected code not found - unsupported game version? Nothing was changed.");
        foreach (var name in Targets) File.Copy(Path.Combine(managed, name + ".orig"), Path.Combine(managed, name), true);
        return 1;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"Patching failed: {e.Message}");
    foreach (var name in Targets) File.Copy(Path.Combine(managed, name + ".orig"), Path.Combine(managed, name), true);
    return 1;
}

Console.WriteLine($"Done. QuiVr now uses {url}");
Console.WriteLine("Run with --restore to undo (Steam's 'Verify integrity of game files' also undoes it).");
return 0;

int PatchModule(string dir, string name, Func<MethodDefinition, int> patch)
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(dir);
    var src = Path.Combine(dir, name + ".orig");
    int count;
    using (var module = ModuleDefinition.ReadModule(src, new ReaderParameters { AssemblyResolver = resolver }))
    {
        count = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).Sum(patch);
        if (count > 0) module.Write(Path.Combine(dir, name));
    }
    Console.WriteLine($"{name}: {count} change(s)");
    return count;
}

int ReplaceUrlLiteral(MethodDefinition method)
{
    int n = 0;
    foreach (var ins in method.Body.Instructions)
    {
        if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == OriginalUrl)
        {
            ins.Operand = url;
            n++;
        }
    }
    return n;
}

int ReplaceAppServerRead(MethodDefinition method)
{
    int n = 0;
    var il = method.Body.GetILProcessor();
    foreach (var ins in method.Body.Instructions.ToList())
    {
        if (ins.OpCode == OpCodes.Ldfld && ins.Operand is FieldReference f
            && f.Name == "AppServer" && f.DeclaringType.Name == "AppValues")
        {
            // stack: AppValues -> string  becomes  AppValues -> (pop) -> url
            var load = il.Create(OpCodes.Ldstr, url);
            ins.OpCode = OpCodes.Pop;
            ins.Operand = null;
            il.InsertAfter(ins, load);
            n++;
        }
    }
    return n;
}

static string? FindManaged(string? dir)
{
    var candidates = new List<string>();
    if (dir != null)
    {
        candidates.Add(dir);
        candidates.Add(Path.Combine(dir, "QuiVr_Data", "Managed"));
        candidates.Add(Path.Combine(dir, "Managed"));
    }
    else
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var steam in new[]
        {
            @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam",
            Path.Combine(home, ".local/share/Steam"), Path.Combine(home, ".steam/steam"),
            Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"),
        })
        {
            candidates.AddRange(SteamLibraries(steam).Select(lib => Path.Combine(lib, "steamapps", "common", "QuiVr", "QuiVr_Data", "Managed")));
        }
    }
    return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "Assembly-CSharp-firstpass.dll")));
}

static IEnumerable<string> SteamLibraries(string steamRoot)
{
    yield return steamRoot;
    var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
    if (!File.Exists(vdf)) yield break;
    foreach (var line in File.ReadLines(vdf))
    {
        var parts = line.Trim().Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(p => p.Length > 0).ToArray();
        if (parts.Length == 2 && parts[0] == "path") yield return parts[1].Replace(@"\\", @"\");
    }
}
