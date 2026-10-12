using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Ro3Emu.ClientTools;

/// <summary>
/// UnityEngine.UI.dll -> Ro3Emu.ClientHooks TextureSwap (English art for Chinese-text images):
/// RawImage.set_texture / Image.set_sprite map their argument, and Graphic.OnEnable reports the graphic so images a
/// prefab serialized are swapped too. All three are plain calls at method entry; the hook returns the argument
/// unchanged for everything it has no replacement for.
/// </summary>
public static class ImageSwapPatch
{
    public static string Patch(ModuleDefinition module, string hooksDll)
    {
        using var hooks = AssemblyDefinition.ReadAssembly(hooksDll, new ReaderParameters { InMemory = true });
        var swap = hooks.MainModule.GetType("Ro3Emu.ClientHooks.TextureSwap") ?? throw new InvalidOperationException("TextureSwap not found in the hooks");
        MethodReference Hook(string name) => module.ImportReference(swap.Methods.Single(m => m.Name == name && m.Parameters.Count == 1));

        var rawSetter = Setter(module, "UnityEngine.UI.RawImage", "set_texture");
        MapArgument(rawSetter, Hook("MapTexture"));
        var imageSetter = Setter(module, "UnityEngine.UI.Image", "set_sprite");
        MapArgument(imageSetter, Hook("MapSprite"));

        var onEnable = module.GetType("UnityEngine.UI.Graphic")?.Methods.SingleOrDefault(m => m.Name == "OnEnable" && m.Parameters.Count == 0)
            ?? throw new InvalidOperationException("Graphic.OnEnable not found");
        var il = onEnable.Body.GetILProcessor();
        var first = onEnable.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, Hook("Enabled")));
        return "image swap: RawImage.texture, Image.sprite, Graphic.OnEnable -> TextureSwap";
    }

    private static MethodDefinition Setter(ModuleDefinition module, string type, string name) =>
        module.GetType(type)?.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Count == 1)
        ?? throw new InvalidOperationException($"{type}.{name} not found");

    /// <summary>arg = hook(arg) at method entry.</summary>
    private static void MapArgument(MethodDefinition method, MethodReference hook)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        var parameter = method.Parameters[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg, parameter));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        il.InsertBefore(first, il.Create(OpCodes.Starg, parameter));
    }
}
