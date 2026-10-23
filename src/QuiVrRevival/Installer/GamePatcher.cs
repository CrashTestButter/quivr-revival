using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace QuiVrRevival.Installer;

public sealed class InstallError(string message) : Exception(message);

/// <summary>
/// Redirects the game to QuiVr Revival. The original server address is compiled into two places:
///   Assembly-CSharp-firstpass.dll  ParseInitBehavior.Awake      string literal (Parse API URL)
///   Assembly-CSharp.dll            SteamSetup.checkServerOnline  reads AppValues.AppServer
/// Both are replaced by a call to QuiVrRevival.Hook.ServerUrl(), which reads the mod's config.ini
/// and can start the local server. Originals are kept as *.orig.
/// </summary>
public static class GamePatcher
{
    public const string OriginalUrl = "http://34.198.149.161:4040/parse/";
    const string FirstPass = "Assembly-CSharp-firstpass.dll";
    const string MainDll = "Assembly-CSharp.dll";
    public const string HookDll = "QuiVrRevival.Hook.dll";

    public static bool IsInstalled(GameDir game) => File.Exists(Path.Combine(game.Managed, HookDll));

    public static void Patch(GameDir game)
    {
        string firstPass = Path.Combine(game.Managed, FirstPass);
        string main = Path.Combine(game.Managed, MainDll);
        PrepareOriginal(firstPass, m => Instructions(m).Any(IsOriginalUrl));
        PrepareOriginal(main, m => Instructions(m).Any(IsAppServerRead));

        byte[] hook = ReadHook();
        var hookName = AssemblyDefinition.ReadAssembly(new MemoryStream(hook)).Name;
        File.WriteAllBytes(Path.Combine(game.Managed, HookDll), hook);

        int a = PatchModule(game.Managed, FirstPass, hookName, (m, call) =>
        {
            int n = 0;
            foreach (var ins in Instructions(m).Where(IsOriginalUrl))
            {
                ins.OpCode = OpCodes.Call;
                ins.Operand = call;
                n++;
            }
            return n;
        });
        int b = PatchModule(game.Managed, MainDll, hookName, (m, call) =>
        {
            int n = 0;
            foreach (var method in m.GetTypes().SelectMany(t => t.Methods).Where(x => x.HasBody))
            {
                var il = method.Body.GetILProcessor();
                foreach (var ins in method.Body.Instructions.Where(IsAppServerRead).ToList())
                {
                    // stack: AppValues -> string   becomes   AppValues -> pop -> ServerUrl()
                    ins.OpCode = OpCodes.Pop;
                    ins.Operand = null;
                    il.InsertAfter(ins, il.Create(OpCodes.Call, call));
                    n++;
                }
            }
            return n;
        });
        if (a == 0 || b == 0)
        {
            Restore(game);
            throw new InstallError("This version of QuiVr is not supported (the server address was not found).");
        }
    }

    /// <summary>Puts the original game files back.</summary>
    public static void Restore(GameDir game)
    {
        foreach (var name in new[] { FirstPass, MainDll })
        {
            string dll = Path.Combine(game.Managed, name);
            if (!File.Exists(dll + ".orig")) continue;
            File.Copy(dll + ".orig", dll, overwrite: true);
            File.Delete(dll + ".orig");
        }
        File.Delete(Path.Combine(game.Managed, HookDll));
    }

    /// <summary>
    /// Keeps a pristine copy as *.orig. The current file is used if it is unpatched (fresh install,
    /// or Steam restored it after "Verify integrity of game files"), otherwise the existing backup.
    /// </summary>
    static void PrepareOriginal(string dll, Func<ModuleDefinition, bool> isOriginal)
    {
        using (var module = Read(dll))
        {
            if (isOriginal(module))
            {
                File.Copy(dll, dll + ".orig", overwrite: true);
                return;
            }
        }
        if (!File.Exists(dll + ".orig"))
            throw new InstallError($"{Path.GetFileName(dll)} is modified and no backup exists. Use Steam's \"Verify integrity of game files\" for QuiVr, then try again.");
        using var backup = Read(dll + ".orig");
        if (!isOriginal(backup))
            throw new InstallError($"The backup of {Path.GetFileName(dll)} is not an original game file. Use Steam's \"Verify integrity of game files\" for QuiVr, then try again.");
    }

    static int PatchModule(string managed, string name, AssemblyNameDefinition hookName, Func<ModuleDefinition, MethodReference, int> patch)
    {
        string path = Path.Combine(managed, name);
        using var module = Read(path + ".orig", managed);
        var asmRef = new AssemblyNameReference(hookName.Name, hookName.Version);
        module.AssemblyReferences.Add(asmRef);
        var hookType = new TypeReference("QuiVrRevival", "Hook", module, asmRef);
        var call = new MethodReference("ServerUrl", module.TypeSystem.String, hookType) { HasThis = false };
        int n = patch(module, call);
        if (n > 0) module.Write(path);
        return n;
    }

    static ModuleDefinition Read(string path, string? searchDir = null)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(searchDir ?? Path.GetDirectoryName(path)!);
        // Read from memory so the file can be overwritten afterwards (Windows locks open files).
        return ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(path)), new ReaderParameters { AssemblyResolver = resolver });
    }

    static IEnumerable<Instruction> Instructions(ModuleDefinition m) =>
        m.GetTypes().SelectMany(t => t.Methods).Where(x => x.HasBody).SelectMany(x => x.Body.Instructions);

    static bool IsOriginalUrl(Instruction ins) => ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == OriginalUrl;

    static bool IsAppServerRead(Instruction ins) =>
        ins.OpCode == OpCodes.Ldfld && ins.Operand is FieldReference { Name: "AppServer", DeclaringType.Name: "AppValues" };

    static byte[] ReadHook()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(HookDll)
            ?? throw new InvalidOperationException("hook resource missing");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
