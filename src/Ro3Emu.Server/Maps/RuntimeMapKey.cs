namespace Ro3Emu.Server.Maps;

/// <summary>Configuration is navigation/content; runtime identity is locality. Never persist this key.
/// Public line 1 and existing private handles retain their legacy numeric WorldScene projection.
/// Additional public lines and events use disjoint negative ranges (inferred server policy).
/// Private room ids are allocated globally above the configuration-id range by InstanceService.</summary>
public enum RuntimeMapKind { PublicLine, PrivateInstance, Event }

public readonly record struct RuntimeMapKey(RuntimeMapKind Kind, long SceneEntry, long Id)
{
    public static RuntimeMapKey Public(long scene, int line = 1) => new(RuntimeMapKind.PublicLine, scene, line);
    public static RuntimeMapKey Private(long scene, long instance) => new(RuntimeMapKind.PrivateInstance, scene, instance);
    public static RuntimeMapKey Event(long scene, long id) => new(RuntimeMapKind.Event, scene, id);
    public int LineNumber => Kind == RuntimeMapKind.PublicLine ? checked((int)Id) : 1;
    public long WorldScene => Kind switch
    {
        RuntimeMapKind.PublicLine when Id == 1 => SceneEntry,
        RuntimeMapKind.PublicLine when SceneEntry is > 0 and <= 0x3fffffff && Id is > 1 and <= int.MaxValue
            => -((SceneEntry << 31) | Id),
        RuntimeMapKind.PrivateInstance when Id > 0 => Id,
        RuntimeMapKind.Event when Id is > 0 and < (1L << 61) => long.MinValue + Id,
        _ => throw new InvalidOperationException("Invalid runtime map identity."),
    };
}
