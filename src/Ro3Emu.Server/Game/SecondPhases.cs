namespace Ro3Emu.Server.Game;

/// <summary>
/// Spreads once-a-second per-session work over the 20 ticks of a second by role id, instead of posting every session
/// in one tick (300 players made a 600-item loop queue burst every second from the daily and AFK reconcilers).
/// Late ticks catch up the skipped phases, at most one full second.
/// </summary>
internal sealed class SecondPhases
{
    public const int Count = 20;
    private readonly bool[] _due = new bool[Count];
    private long _lastSlot = -1;

    /// <summary>Which phases are due at this tick; null when none (the same tick twice).</summary>
    public bool[]? Due(TimeSpan now)
    {
        long slot = (long)(now.TotalMilliseconds / GameLoop.TickInterval.TotalMilliseconds);
        if (slot == _lastSlot) return null;
        Array.Clear(_due);
        // The first tick covers every phase (nobody waits up to a second after start); late ticks catch up.
        long from = _lastSlot < 0 ? slot - (Count - 1) : Math.Max(_lastSlot + 1, slot - (Count - 1));
        for (long s = from; s <= slot; s++) _due[(int)(((s % Count) + Count) % Count)] = true;
        _lastSlot = slot;
        return _due;
    }

    public static int PhaseOf(GameSession session) => (int)((ulong)(session.Role?.Id ?? 0) % Count);
}
