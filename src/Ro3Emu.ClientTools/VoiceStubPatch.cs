using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Assembly-CSharp NetworkLib.VoiceCallAdapter is the client's Tencent GME voice (tavern and guild voice rooms, voice
/// messages) on the publisher's GME account: app id and key are literals in the class, the auth buffer is made from them
/// locally, and VoiceCall_Util.Init(roleId) runs in the IM ConnectToServer, which our chat redirect lets through. Audio is
/// off in this PC build (LA_Utils.USE_YIM false), but Init, JoinChannel and LeaveChannel still went to GME.
/// The adapter becomes a stub that never touches the GME wrapper (gmesdk): Init only keeps the role id, JoinChannel
/// reports the join through the adapter's own success path (Obf_PYC: Lua OnJoinChannel(channel, user, 0, "OK."), what
/// the original does for a room it is already in) so the guild voice UI records the channel, LeaveChannel reports the
/// leave (Lua OnLeaveChannel), LeaveChannelAll reports OnLeaveChannelAll; every other call is a no-op. The constructor
/// no longer creates the GME context. Repeating the patch on its own output changes nothing.
/// </summary>
public static class VoiceStubPatch
{
    public const string AdapterType = "NetworkLib.VoiceCallAdapter";

    /// <summary>The native libraries of the GME SDK (P/Invoke module names, compared by prefix).</summary>
    private static readonly string[] NativeModules = ["gmesdk", "libgme", "TXSoundTouch", "TXFFmpeg"];

    /// <summary>ReturnCode.NotInit, for the voice message (PTT) calls nothing uses with our chat.</summary>
    private const int NotInit = 1;

    /// <summary>Stubs with a fixed result, by obfuscated name and parameter count (Lua names from the binding table).</summary>
    private static readonly (string Name, int Parameters, object? Result)[] Fixed =
    [
        ("Obf_YyC", 1, 0),        // Login
        ("Obf_zyC", 0, true),     // GME login after Init
        ("Obf_ZyC", 0, null),     // OnUpdateLogic (GME Poll)
        ("Obf_aYC", 0, null),     // destroy the sub instances
        ("Obf_CYC", 1, null),     // GetChannel
        ("Obf_eYC", 3, null),     // subscribe the room events
        ("Obf_FYC", 1, true),     // SpeakToChannelID
        ("Obf_hYC", 2, true),     // SetSpeakerMute
        ("Obf_HYC", 1, false),    // GetSpeakerMute
        ("Obf_iYC", 2, true),     // SetMicrophoneMute
        ("Obf_IYC", 1, false),    // GetMicrophoneMute
        ("Obf_jYC", 2, true),     // SetSpeakerVolume
        ("Obf_JYC", 2, true),     // SetMicVolume
        ("Obf_kYC", 1, -1),       // GetSpeakerVolume
        ("Obf_KYC", 1, -1),       // GetMicVolume
        ("Obf_oYC", 2, true),     // AddListenBlackList
        ("Obf_OYC", 2, true),     // RemoveListenBlackList
        ("Obf_pYC", 2, ""),       // GetUserSign (the auth buffer)
        ("Obf_TYC", 1, NotInit),  // StartRecording
        ("Obf_uYC", 0, NotInit),  // StopRecording
        ("Obf_UYC", 0, NotInit),  // CancelRecording
        ("Obf_vYC", 1, null),     // AddRecordFileCompleteCallback
        ("Obf_VYC", 1, null),     // AddUploadFileCompleteCallback
        ("Obf_wYC", 1, null),     // AddDownloadFileCompleteCallback
        ("Obf_WYC", 1, null),     // AddPlayFileCompleteCallback
        ("Obf_xYC", 1, null),     // RemoveRecordFileCompleteCallback
        ("Obf_XYC", 1, null),     // RemovePlayFileCompleteCallback
        ("Obf_yYC", 1, NotInit),  // UploadRecordedFile
        ("Obf_YYC", 2, NotInit),  // DownloadRecordedFile
        ("Obf_zYC", 1, NotInit),  // PlayRecordedFile
        ("Obf_ZYC", 0, NotInit),  // StopPlayFile
        ("Obf_azC", 1, 0),        // GetFileSize
        ("Obf_AzC", 1, 0),        // GetVoiceFileDuration
    ];

    public static string Patch(ModuleDefinition module)
    {
        var adapter = module.GetType(AdapterType) ?? throw new InvalidOperationException("VoiceCallAdapter not found");
        var native = NativeNamespaces(module);
        if (native.Count == 0) throw new InvalidOperationException("GME P/Invokes not found");

        MethodDefinition Method(string name, int parameters) =>
            adapter.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Count == parameters)
            ?? throw new InvalidOperationException($"VoiceCallAdapter.{name}({parameters}) not found");
        FieldDefinition Field(string name) =>
            adapter.Fields.SingleOrDefault(f => f.Name == name) ?? throw new InvalidOperationException($"VoiceCallAdapter.{name} not found");

        var userId = Field("Obf_Zeb");          // string: the role id Init got
        var joined = Field("Obf_zeb");          // Dictionary<string, bool>: channel -> in the room
        var leaveAllPending = Field("Obf_CEb"); // LeaveChannelAll waits for the last leave
        var toLua = Method("Obf_XyC", 2);       // VoiceCall_Util[name](args...)
        var leftAllCheck = Method("Obf_QYC", 0); // OnLeaveChannelAll once nothing is joined
        var init = Method("Obf_yyC", 1);
        var uninit = Method("Obf_AYC", 0);
        var join = Method("Obf_dYC", 3);
        var joinedCallback = Method("Obf_PYC", 5);
        var leave = Method("Obf_gYC", 1);
        var leftCallback = Method("Obf_qYC", 3);
        var leaveAll = Method("Obf_GYC", 0);
        var inAnyChannel = Method("Obf_fYC", 0);
        if (joined.FieldType is not GenericInstanceType dictionary || dictionary.GenericArguments.Count != 2)
            throw new InvalidOperationException("VoiceCallAdapter.Obf_zeb is not a Dictionary<string, bool>");

        var types = module.TypeSystem;
        // Dictionary`2's own !0 / !1 for the member signatures (a type reference carries no generic parameters until given).
        var open = dictionary.ElementType;
        while (open.GenericParameters.Count < 2) open.GenericParameters.Add(new GenericParameter(open));
        var key = open.GenericParameters[0];
        var value = open.GenericParameters[1];
        MethodReference Dictionary(string name, TypeReference returns, params TypeReference[] parameters)
        {
            var m = new MethodReference(name, returns, dictionary) { HasThis = true };
            foreach (var p in parameters) m.Parameters.Add(new ParameterDefinition(p));
            return m;
        }
        var dictionaryCtor = Dictionary(".ctor", types.Void);
        var setItem = Dictionary("set_Item", types.Void, key, value);
        var remove = Dictionary("Remove", types.Boolean, key);
        var clear = Dictionary("Clear", types.Void);
        var count = Dictionary("get_Count", types.Int32);
        var isNullOrEmpty = new MethodReference("IsNullOrEmpty", types.Boolean, types.String);
        isNullOrEmpty.Parameters.Add(new ParameterDefinition(types.String));

        foreach (var (name, parameters, result) in Fixed) Return(Method(name, parameters), result);

        // Init(roleId): if (string.IsNullOrEmpty(roleId)) return false; userId = roleId; return true;
        Rewrite(init, il =>
        {
            var go = il.Create(OpCodes.Ldarg_0);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Call, isNullOrEmpty));
            il.Append(il.Create(OpCodes.Brfalse, go));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));
            il.Append(go);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, userId));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
        });

        // UnInit(): userId = null; joined.Clear(); return true;
        Rewrite(uninit, il =>
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Stfld, userId));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, joined));
            il.Append(il.Create(OpCodes.Callvirt, clear));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
        });

        // Obf_PYC(room, channelId, userId, code, msg): joined[channelId] = code == 0; Lua OnJoinChannel(channelId, userId, code, msg).
        // (The original asked the GME room whether it is entered.)
        Rewrite(joinedCallback, il =>
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, joined));
            il.Append(il.Create(OpCodes.Ldarg, joinedCallback.Parameters[1]));
            il.Append(il.Create(OpCodes.Ldarg, joinedCallback.Parameters[3]));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ceq));
            il.Append(il.Create(OpCodes.Callvirt, setItem));
            CallLua(il, types, toLua, "OnJoinChannel",
                i => i.Append(i.Create(OpCodes.Ldarg, joinedCallback.Parameters[1])),
                i => i.Append(i.Create(OpCodes.Ldarg, joinedCallback.Parameters[2])),
                i =>
                {
                    i.Append(i.Create(OpCodes.Ldarg, joinedCallback.Parameters[3]));
                    i.Append(i.Create(OpCodes.Box, types.Int32));
                },
                i => i.Append(i.Create(OpCodes.Ldarg, joinedCallback.Parameters[4])));
            il.Append(il.Create(OpCodes.Ret));
        });

        // JoinChannel(channelId, roomType, authBuffer): if (channelId == null) return false; Obf_PYC(null, channelId, userId, 0, "OK."); return true;
        Rewrite(join, il =>
        {
            var go = il.Create(OpCodes.Ldarg_0);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Brtrue, go));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));
            il.Append(go);
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, userId));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ldstr, "OK."));
            il.Append(il.Create(OpCodes.Call, joinedCallback));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
        });

        // Obf_qYC(room, channelId, userId): joined.Remove(channelId); Lua OnLeaveChannel(channelId); Obf_QYC();
        // (The original also counted the room in the paired-call tracker, which our join never enters.)
        Rewrite(leftCallback, il =>
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, joined));
            il.Append(il.Create(OpCodes.Ldarg, leftCallback.Parameters[1]));
            il.Append(il.Create(OpCodes.Callvirt, remove));
            il.Append(il.Create(OpCodes.Pop));
            CallLua(il, types, toLua, "OnLeaveChannel", i => i.Append(i.Create(OpCodes.Ldarg, leftCallback.Parameters[1])));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, leftAllCheck));
            il.Append(il.Create(OpCodes.Ret));
        });

        // LeaveChannel(channelId): if (channelId == null) return false; Obf_qYC(null, channelId, userId); return true;
        Rewrite(leave, il =>
        {
            var go = il.Create(OpCodes.Ldarg_0);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Brtrue, go));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));
            il.Append(go);
            il.Append(il.Create(OpCodes.Ldnull));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, userId));
            il.Append(il.Create(OpCodes.Call, leftCallback));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
        });

        // LeaveChannelAll(): leaveAllPending = true; joined.Clear(); Obf_QYC(); return true;
        // Only OnLeaveChannelAll is reported: Lua registers no OnLeaveChannel callback for it.
        Rewrite(leaveAll, il =>
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Stfld, leaveAllPending));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, joined));
            il.Append(il.Create(OpCodes.Callvirt, clear));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, leftAllCheck));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Ret));
        });

        // IsInAnyChannel(): return joined.Count > 0;
        Rewrite(inAnyChannel, il =>
        {
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, joined));
            il.Append(il.Create(OpCodes.Callvirt, count));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Cgt));
            il.Append(il.Create(OpCodes.Ret));
        });

        // The constructor: no GME context (field initializer Obf_yeb = Obf_VD.Obf_nnA() -> null), and the joined map exists
        // from the start (the original made it in Init; IsInChannel reads it).
        foreach (var ctor in adapter.Methods.Where(m => m.IsConstructor && !m.IsStatic))
        {
            foreach (var call in ctor.Body.Instructions.Where(i => i.Operand is MethodReference m && IsNative(m.DeclaringType, native)).ToList())
            {
                var target = (MethodReference)call.Operand;
                if (call.OpCode != OpCodes.Call || target.HasThis || target.Parameters.Count != 0 || target.ReturnType.IsValueType)
                    throw new InvalidOperationException($"VoiceCallAdapter constructor: unexpected GME call {target.FullName}");
                call.OpCode = OpCodes.Ldnull;
                call.Operand = null;
            }
            if (!ctor.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == joined.Name))
            {
                var il = ctor.Body.GetILProcessor();
                var first = ctor.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Newobj, dictionaryCtor));
                il.InsertBefore(first, il.Create(OpCodes.Stfld, joined));
            }
        }

        var left = NativeCalls(module);
        if (left.Count > 0) throw new InvalidOperationException("VoiceCallAdapter still reaches GME: " + string.Join(", ", left));
        return $"voice -> stub ({Fixed.Length + 9} methods, no GME)";
    }

    /// <summary>
    /// Every "method -> GME member" edge reachable from any VoiceCallAdapter method or constructor, following calls
    /// into the adapter's own methods (and its nested types). A GME member is one of the types in the namespaces that
    /// declare the GME P/Invokes, or a P/Invoke into a GME library itself. Empty for the patched adapter.
    /// </summary>
    public static List<string> NativeCalls(ModuleDefinition module, IEnumerable<string>? entryNames = null)
    {
        var adapter = module.GetType(AdapterType) ?? throw new InvalidOperationException("VoiceCallAdapter not found");
        var native = NativeNamespaces(module);
        var queue = new Queue<MethodDefinition>(adapter.Methods.Where(m => entryNames == null || m.IsConstructor || entryNames.Contains(m.Name)));
        var seen = new HashSet<MethodDefinition>(queue);
        var found = new List<string>();
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (!method.HasBody) continue;
            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case MethodReference target when IsNative(target.DeclaringType, native) || IsNativeImport(target):
                        found.Add($"{method.Name} -> {target.DeclaringType.Name}::{target.Name}");
                        break;
                    case FieldReference field when IsNative(field.DeclaringType, native):
                        found.Add($"{method.Name} -> {field.DeclaringType.Name}::{field.Name}");
                        break;
                    case MethodDefinition own when Within(own.DeclaringType, adapter) && seen.Add(own):
                        queue.Enqueue(own);
                        break;
                }
            }
        }
        return found;
    }

    /// <summary>The namespaces of the types that declare P/Invokes into the GME libraries (the GME wrapper).</summary>
    private static HashSet<string> NativeNamespaces(ModuleDefinition module) =>
        module.GetTypes().Where(t => t.Methods.Any(IsNativeImport)).Select(t => Outer(t).Namespace).ToHashSet(StringComparer.Ordinal);

    private static bool IsNativeImport(MethodReference method) =>
        method is MethodDefinition { IsPInvokeImpl: true, PInvokeInfo: { } info }
        && NativeModules.Any(n => info.Module.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase));

    private static bool IsNative(TypeReference type, HashSet<string> native) =>
        type is not ArrayType and not GenericParameter && native.Contains(Outer(type).Namespace);

    private static TypeReference Outer(TypeReference type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type is TypeSpecification spec ? Outer(spec.ElementType) : type;
    }

    private static bool Within(TypeDefinition type, TypeDefinition outer)
    {
        for (var t = type; t != null; t = t.DeclaringType)
            if (t == outer) return true;
        return false;
    }

    /// <summary>this.Obf_XyC(name, new object[] { args... }).</summary>
    private static void CallLua(ILProcessor il, TypeSystem types, MethodReference toLua, string name, params Action<ILProcessor>[] arguments)
    {
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldstr, name));
        il.Append(il.Create(OpCodes.Ldc_I4, arguments.Length));
        il.Append(il.Create(OpCodes.Newarr, types.Object));
        for (int index = 0; index < arguments.Length; index++)
        {
            il.Append(il.Create(OpCodes.Dup));
            il.Append(il.Create(OpCodes.Ldc_I4, index));
            arguments[index](il);
            il.Append(il.Create(OpCodes.Stelem_Ref));
        }
        il.Append(il.Create(OpCodes.Call, toLua));
    }

    /// <summary>Replaces the body: the method returns <paramref name="result"/> (null: void or a null reference).</summary>
    private static void Return(MethodDefinition method, object? result)
    {
        var returns = method.ReturnType.MetadataType;
        Rewrite(method, il =>
        {
            switch (result)
            {
                case null when returns == MetadataType.Void:
                    break;
                case null when !method.ReturnType.IsValueType:
                    il.Append(il.Create(OpCodes.Ldnull));
                    break;
                case bool b when returns == MetadataType.Boolean:
                    il.Append(il.Create(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                    break;
                case int i when returns == MetadataType.Int32:
                    il.Append(il.Create(OpCodes.Ldc_I4, i));
                    break;
                case string s when returns == MetadataType.String:
                    il.Append(il.Create(OpCodes.Ldstr, s));
                    break;
                default:
                    throw new InvalidOperationException($"VoiceCallAdapter.{method.Name}: {method.ReturnType.FullName} is not {result?.GetType().Name ?? "void"}");
            }
            il.Append(il.Create(OpCodes.Ret));
        });
    }

    private static void Rewrite(MethodDefinition method, Action<ILProcessor> emit)
    {
        method.Body.Instructions.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        method.Body.InitLocals = false;
        emit(method.Body.GetILProcessor());
        method.Body.OptimizeMacros();
    }
}
