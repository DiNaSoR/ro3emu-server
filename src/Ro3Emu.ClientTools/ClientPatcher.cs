using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Repoints the RO3 client (the PC build) at an emulator by rewriting string literals in managed DLLs:
///   SDK login URL and server list URL (Happyengine.Network Obf_dI + callers, Assembly-CSharp MetaSDKUtil);
///   gate RSA public key (Phonest.Common Utils.GenEncryptKeyFromToken).
/// Originals are copied once to &lt;client&gt;\ro3emu-backup and every patch starts from them, so it is repeatable.
/// Lua and proto are untouched (they are RSA-signed and checked by RO3.ClientSecurity).
/// </summary>
public static class ClientPatcher
{
    private const string OriginalLoginUrl = "http://52.8.99.195/sdk/login";
    private const string OriginalServerListUrl = "https://server.playragnarok3.com/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true";
    private const string ServerListPathAndQuery = "/serverlist/v2/168/serverlist.json?inline=true&force_refresh=true";
    private const string PemMarker = "-----BEGIN RSA PUBLIC KEY-----";

    private static readonly string[] PatchedFiles = ["Happyengine.Network.dll", "Assembly-CSharp.dll", "Phonest.Common.dll", "Happyengine.Base.Runtime.dll", "Unity.TextMeshPro.dll", "HUD.dll", "HappyEngine.Resource.dll", "UnityEngine.UI.dll"];

    /// <summary>--verbose: GS_Debug.ShowLog always true, so the Lua debug logs (Lua_Debug reads it) reach Player.log.</summary>
    public static bool VerboseLogs { get; set; }

    private static string ForceShowLog(ModuleDefinition module)
    {
        var debug = module.GetType("HappyEngine.GS_Debug") ?? throw new InvalidOperationException("GS_Debug not found");
        var getter = debug.Properties.Single(p => p.Name == "ShowLog").GetMethod;
        getter.Body.Instructions.Clear();
        var il = getter.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Ret));
        return "GS_Debug.ShowLog -> true";
    }

    private const string HooksFile = "Ro3Emu.ClientHooks.dll";
    private const string LocFile = "Ro3Emu.Loc.dll";

    public static void Patch(string clientDir, string serverBaseUrl, string publicKeyPem, string? hooksDll = null)
    {
        var managed = ManagedDir(clientDir);
        var backup = BackupDir(clientDir);
        Directory.CreateDirectory(backup);
        foreach (var file in PatchedFiles)
        {
            var saved = Path.Combine(backup, file);
            if (!File.Exists(saved))
            {
                File.Copy(Path.Combine(managed, file), saved);
                Console.WriteLine($"backed up {file}");
            }
        }

        string pem = publicKeyPem.Replace("\r\n", "\n").Trim();
        if (!pem.StartsWith(PemMarker, StringComparison.Ordinal)) throw new ArgumentException("the key file is not a PKCS#1 'RSA PUBLIC KEY' PEM");

        var rules = new List<Rule>
        {
            new(OriginalLoginUrl, serverBaseUrl + "/sdk/login", Exact: true),
            new(OriginalServerListUrl, serverBaseUrl + ServerListPathAndQuery, Exact: true),
            new(PemMarker, pem, Exact: false),
        };
        hooksDll ??= DefaultHooksDll();
        if (!File.Exists(hooksDll)) throw new FileNotFoundException("build src/Ro3Emu.ClientHooks first", hooksDll);
        File.Copy(hooksDll, Path.Combine(managed, HooksFile), overwrite: true);
        Console.WriteLine($"installed {HooksFile}");
        var locDll = Embedded(LocFile, "Ro3Emu.Loc");
        if (!File.Exists(locDll)) throw new FileNotFoundException("build src/Ro3Emu.Loc first", locDll);
        File.Copy(locDll, Path.Combine(managed, LocFile), overwrite: true);
        Console.WriteLine($"installed {LocFile}");

        foreach (var file in PatchedFiles)
        {
            Func<ModuleDefinition, string>? extra = file switch
            {
                "Assembly-CSharp.dll" => m => RedirectChat(m, hooksDll) + "; " + TextSink.PatchLanguageGet(m, locDll),
                "Happyengine.Base.Runtime.dll" when VerboseLogs => ForceShowLog,
                "Unity.TextMeshPro.dll" => m => TextSink.PatchTmp(m, locDll, hooksDll),
                "HUD.dll" => m => TextSink.PatchHud(m, locDll),
                "HappyEngine.Resource.dll" => m => TextSink.PatchLauncher(m, locDll),
                "UnityEngine.UI.dll" => m => ImageSwapPatch.Patch(m, hooksDll),
                _ => null,
            };
            RewriteStrings(clientDir, file, rules, extra);
        }

        var missing = rules.Where(r => r.Hits == 0).Select(r => r.Find).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"literal not found: {string.Join(" | ", missing)} (different client build?)");
        Console.WriteLine($"client now talks to {serverBaseUrl}; do not run RO3Launcher.exe on this copy (its repair restores the originals)");
    }

    public static void Restore(string clientDir)
    {
        var managed = ManagedDir(clientDir);
        var backup = BackupDir(clientDir);
        foreach (var file in PatchedFiles)
        {
            var saved = Path.Combine(backup, file);
            if (!File.Exists(saved)) continue;
            File.Copy(saved, Path.Combine(managed, file), overwrite: true);
            Console.WriteLine($"restored {file}");
        }
        foreach (var extra in new[] { HooksFile, LocFile })
        {
            var installed = Path.Combine(managed, extra);
            if (!File.Exists(installed)) continue;
            File.Delete(installed);
            Console.WriteLine($"removed {extra}");
        }
    }

    /// <summary>Lists every string literal in every managed DLL that contains one of the needles.</summary>
    public static void Scan(string clientDir, string[] needles)
    {
        var managed = ManagedDir(clientDir);
        foreach (var path in Directory.EnumerateFiles(managed, "*.dll"))
        {
            AssemblyDefinition assembly;
            try { assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true }); }
            catch (BadImageFormatException) { continue; }
            using (assembly)
            {
                foreach (var (where, value) in Literals(assembly))
                {
                    if (needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"{Path.GetFileName(path)}  {where}  \"{Shorten(value, 120)}\"");
                }
            }
        }
    }

    /// <summary>Lists the methods of a type (optionally only those named <paramref name="method"/>) in a client DLL.</summary>
    public static void Members(string clientDir, string dll, string type, string? method)
    {
        var path = File.Exists(Path.Combine(BackupDir(clientDir), dll)) ? Path.Combine(BackupDir(clientDir), dll) : Path.Combine(ManagedDir(clientDir), dll);
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        foreach (var t in assembly.MainModule.GetTypes().Where(t => t.FullName == type || t.Name == type))
            foreach (var m in t.Methods.Where(m => method == null || m.Name == method))
                Console.WriteLine($"{t.FullName}::{m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name))}) static={m.IsStatic} body={m.HasBody}");
    }

    private sealed record Rule(string Find, string Replacement, bool Exact)
    {
        public int Hits { get; set; }

        public bool Matches(string value) => Exact ? value == Find : value.Contains(Find, StringComparison.Ordinal);
    }

    /// <summary>
    /// Chat runs on Tencent IM only. NW_ChatNetworkManager.Init (Obf_iXC) is rewritten to wrap our
    /// ServerImAdapter instead of the Tencent adapter, and the server app-id check (Obf_IXC, Lua ValidateServerSdkAppId)
    /// always passes. Everything Lua sees stays the same.
    /// </summary>
    private static string RedirectChat(ModuleDefinition module, string hooksDll)
    {
        var manager = module.GetType("NetworkLib.NW_ChatNetworkManager") ?? throw new InvalidOperationException("NW_ChatNetworkManager not found");
        var init = manager.Methods.Single(m => m.Name == "Obf_iXC" && m.Parameters.Count == 0);
        var validate = manager.Methods.Single(m => m.Name == "Obf_IXC" && m.Parameters.Count == 1);
        var wrapper = manager.Fields.Single(f => f.Name == "Obf_rDb");
        var active = manager.Fields.Single(f => f.Name == "Obf_tDb");
        var wrapperCtor = module.ImportReference(wrapper.FieldType.Resolve().Methods.Single(m => m.IsConstructor && m.Parameters.Count == 1));

        using var hooks = AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
        var adapterCtor = module.ImportReference(hooks.MainModule.GetType("Ro3Emu.ClientHooks.ServerImAdapter").Methods.Single(m => m.IsConstructor && !m.IsStatic));

        // Init(): if (!active) { wrapper = new Obf_Ci(new ServerImAdapter()); active = true; }
        init.Body.Instructions.Clear();
        init.Body.ExceptionHandlers.Clear();
        init.Body.Variables.Clear();
        var il = init.Body.GetILProcessor();
        var end = il.Create(OpCodes.Ret);
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldfld, active));
        il.Append(il.Create(OpCodes.Brtrue, end));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Newobj, adapterCtor));
        il.Append(il.Create(OpCodes.Newobj, wrapperCtor));
        il.Append(il.Create(OpCodes.Stfld, wrapper));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_1));
        il.Append(il.Create(OpCodes.Stfld, active));
        il.Append(end);

        // ValidateServerSdkAppId(string): return true;
        validate.Body.Instructions.Clear();
        validate.Body.ExceptionHandlers.Clear();
        validate.Body.Variables.Clear();
        var vil = validate.Body.GetILProcessor();
        vil.Append(vil.Create(OpCodes.Ldc_I4_1));
        vil.Append(vil.Create(OpCodes.Ret));
        return "chat -> ServerImAdapter";
    }

    /// <summary>The hooks DLL ships inside this tool (EmbeddedResource); a repo build is the fallback for development.</summary>
    private static string DefaultHooksDll() => Embedded(HooksFile, "Ro3Emu.ClientHooks");

    /// <summary>A client-side DLL embedded in this tool, extracted to a temp file; a repo build is the fallback.</summary>
    private static string Embedded(string fileName, string project)
    {
        using (var embedded = typeof(ClientPatcher).Assembly.GetManifestResourceStream(fileName))
        {
            if (embedded != null)
            {
                var extracted = Path.Combine(Path.GetTempPath(), "ro3emu-" + fileName);
                using (var file = File.Create(extracted)) embedded.CopyTo(file);
                return extracted;
            }
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", project, "bin", "Debug", "netstandard2.1", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    /// <summary>Loads the pristine DLL from the backup, rewrites matching literals, writes it into Managed if anything changed.</summary>
    private static void RewriteStrings(string clientDir, string file, List<Rule> rules, Func<ModuleDefinition, string>? extra = null)
    {
        using var resolver = new DefaultAssemblyResolver(); // disposing closes the DLLs it opened in Managed
        resolver.AddSearchDirectory(ManagedDir(clientDir));
        var source = Path.Combine(BackupDir(clientDir), file);
        using var assembly = AssemblyDefinition.ReadAssembly(source, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
        var changed = new List<string>();

        string? Replace(string where, string value)
        {
            var rule = rules.FirstOrDefault(r => r.Matches(value));
            if (rule == null) return null;
            rule.Hits++;
            changed.Add(where);
            return rule.Replacement;
        }

        foreach (var type in assembly.MainModule.GetTypes())
        {
            foreach (var field in type.Fields)
            {
                if (field.HasConstant && field.Constant is string s && Replace(field.FullName, s) is { } r) field.Constant = r;
            }
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string s && Replace(method.FullName, s) is { } r) instruction.Operand = r;
                }
            }
        }

        if (extra != null) changed.Add(extra(assembly.MainModule));

        if (changed.Count == 0)
        {
            File.Copy(source, Path.Combine(ManagedDir(clientDir), file), overwrite: true);
            Console.WriteLine($"{file}: nothing to patch (original kept)");
            return;
        }
        assembly.Write(Path.Combine(ManagedDir(clientDir), file));
        Console.WriteLine($"patched {file}: {changed.Count} literal(s)");
        foreach (var where in changed) Console.WriteLine($"    {Shorten(where, 150)}");
    }

    private static IEnumerable<(string Where, string Value)> Literals(AssemblyDefinition assembly)
    {
        foreach (var type in assembly.MainModule.GetTypes())
        {
            foreach (var field in type.Fields)
                if (field.HasConstant && field.Constant is string s) yield return (field.FullName, s);
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instruction in method.Body.Instructions)
                    if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string s) yield return (method.FullName, s);
            }
        }
    }

    private static string Shorten(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    private static string ManagedDir(string clientDir)
    {
        var managed = Path.Combine(clientDir, "ro3_Data", "Managed");
        if (!File.Exists(Path.Combine(clientDir, "ro3.exe")) || !Directory.Exists(managed))
            throw new DirectoryNotFoundException($"{clientDir} is not an RO3 client folder (expected ro3.exe and ro3_Data\\Managed)");
        return managed;
    }

    private static string BackupDir(string clientDir) => Path.Combine(clientDir, "ro3emu-backup");
}
