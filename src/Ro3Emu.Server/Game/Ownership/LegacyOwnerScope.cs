namespace Ro3Emu.Server.Game.Ownership;

/// <summary>Checked capability for nested legacy helpers; unrelated Post/TrackDetached work never inherits it.</summary>
public sealed class OwnerContext
{
    internal OwnerContext(OwnerRegistry registry, long operation, long admissionSequence, string origin, bool runtimeOnly = false)
    { Registry = registry; Operation = operation; AdmissionSequence = admissionSequence; Origin = origin; RuntimeOnly = runtimeOnly; }
    internal OwnerRegistry Registry { get; }
    public long Operation { get; }
    internal long AdmissionSequence { get; }
    public string Origin { get; }
    // Runtime intent retains loop/completion identity, but cannot authorize a persistence mutation.
    internal bool RuntimeOnly { get; }
    internal bool Active = true;
    /// <summary>Stopwatch timestamp of a Realm admission (0 when the scope holds no Realm), for the held-time metric.</summary>
    internal long RealmAdmittedAt { get; init; }
    /// <summary>The Realm metric's label (the origin, or a finer one such as the dispatched message id).</summary>
    internal string? RealmLabel { get; init; }
    internal OwnerNotices? Notices;
    internal Func<bool>? RequestCurrent;
    internal GameSession? RequestSession;
    internal void AssertRequest()
    {
        if (RequestCurrent?.Invoke() == false)
            throw new OperationCanceledException("Economic request attachment changed before admission.");
    }
    internal bool CanPresent(GameSession session) => !ReferenceEquals(RequestSession, session) || RequestCurrent?.Invoke() != false;
    private readonly Dictionary<long, long> _epochs = [];
    private readonly Dictionary<long, RoleOwner> _pinned = [];
    private readonly HashSet<long> _cleanup = [];
    internal void PermitCleanup(RoleOwner owner, AttachmentLease lease)
    {
        Registry.AssertCurrent(this);
        if (owner.State != RoleOwnerState.Quiescing || !ReferenceEquals(owner.Attachment, lease))
            throw new InvalidOperationException("Cleanup capability requires the current quiescing attachment.");
        _cleanup.Add(owner.RoleId);
    }
    internal void Pin(RoleOwner owner)
    {
        Registry.AssertCurrent(this);
        if (RuntimeOnly) throw new InvalidOperationException("Runtime intent cannot pin a durable role mutation.");
        if (!CanPin(owner))
            throw new OperationCanceledException($"Role {owner.RoleId} is not admitting new mutations.");
        if (_epochs.TryGetValue(owner.RoleId, out long epoch) && epoch != owner.LifecycleEpoch)
            throw new InvalidOperationException("An admitted operation cannot change its role epoch.");
        _epochs[owner.RoleId] = owner.LifecycleEpoch;
        if (_pinned.TryAdd(owner.RoleId, owner)) owner.Operations.Add(Operation);
    }
    /// <summary>Read-only admission probe for multi-role plans that must decide every participant
    /// before claiming a shared outcome. Pin remains the authoritative mutation admission.</summary>
    internal bool CanPin(RoleOwner owner)
    {
        Registry.AssertCurrent(this);
        if (RuntimeOnly || owner.State is RoleOwnerState.Deleting or RoleOwnerState.Deleted or RoleOwnerState.RecoveryRequired
            || owner.State == RoleOwnerState.Quiescing && AdmissionSequence > owner.QuiesceAdmission && !_cleanup.Contains(owner.RoleId))
            return false;
        if (_epochs.TryGetValue(owner.RoleId, out long epoch) && epoch != owner.LifecycleEpoch)
            throw new InvalidOperationException("An admitted operation cannot change its role epoch.");
        return true;
    }
    internal void UnpinAll()
    {
        foreach (var owner in _pinned.Values) owner.OperationFinished(Operation);
        _pinned.Clear();
    }
    internal long? ExpectedRoleEpoch(long roleId)
    {
        Registry.AssertCurrent(this);
        return _epochs.TryGetValue(roleId, out long epoch) ? epoch : null;
    }
    public void AssertRole(long id)
    {
        Registry.AssertCurrent(this);
        if (!Registry.Reservations.Covers(Operation, [ReservationKey.Role(id)]) &&
            !Registry.AuditUncovered($"{Origin} touches role {id} without reserving it"))
            throw new InvalidOperationException($"{Origin} did not reserve role {id} before entering its legacy gate.");
    }
    internal void BindPresentation(GameSession session)
    {
        AssertRole(session.Role!.Id);
        Registry.BindPresentation(this, session);
    }
    internal void AssertPart(long id, string part)
    {
        Registry.AssertCurrent(this);
        if (!Registry.Reservations.Covers(Operation, [ReservationKey.Role(id, part)]) &&
            !Registry.AuditUncovered($"{Origin} writes role {id} part {part} without reserving it"))
            throw new InvalidOperationException($"{Origin} did not reserve role {id} part {part} before writer admission.");
    }
}

/// <summary>Reserves before old semaphores; synchronous writer behavior and StateLock remain unchanged.</summary>
public sealed class LegacyOwnerScope : IDisposable
{
    private readonly OwnerRegistry _registry;
    private readonly GameLoop.WorkPermit _permit;
    private readonly OwnerContext? _previous;
    private readonly bool _owns;
    private bool _disposed;
    public OwnerContext Context { get; }
    internal LegacyOwnerScope(OwnerRegistry registry, GameLoop.WorkPermit permit, OwnerContext context, bool owns)
    {
        _registry = registry; _permit = permit; Context = context; _owns = owns;
        _previous = permit.Ownership; permit.Ownership = context;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _registry.AssertLoop(); // reservations are loop-only state
        _disposed = true;
        // A scope disposed out of order still releases its keys: a leaked reservation (Realm above all) would stall
        // every later admission. The misuse is reported after the cleanup.
        bool current = Context.Active && Context.Registry == _registry && ReferenceEquals(_registry.Current, Context);
        if (current) _permit.Ownership = _previous;
        if (_owns && Context.Active)
        {
            Context.UnpinAll(); Context.Active = false; _registry.Reservations.Release(Context.Operation);
            _registry.Forget(Context.Operation);
            if (Context.RealmAdmittedAt != 0) _registry.RealmReleased(Context);
        }
        if (!current) throw new InvalidOperationException("Owner capability is inactive or belongs to another operation.");
    }
}
