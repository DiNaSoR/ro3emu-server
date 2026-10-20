namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    private sealed class StatsMemo
    {
        public readonly Dictionary<GameSession, StatSheet> Sheets = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<GameSession> Building = new(ReferenceEqualityComparer.Instance);
    }

    private readonly AsyncLocal<StatsMemo?> _statsMemo = new();

    /// <summary>
    /// Shares each session's stat sheet across the <see cref="Stats"/> calls of one step that does not change stats
    /// (for example rolling a debuff's tags, which reads both fighters' sheets once per tag). The memo flows only into code called
    /// from that step, so other work running at the same time still builds fresh sheets. Callers must not change a
    /// shared sheet. Dispose to end it; a nested call reuses the outer memo.
    /// </summary>
    public IDisposable ReuseStats()
    {
        if (_statsMemo.Value != null) return NoScope.Instance;
        _statsMemo.Value = new StatsMemo();
        return new Scope(_statsMemo);
    }

    private sealed class Scope(AsyncLocal<StatsMemo?> slot) : IDisposable
    {
        public void Dispose() => slot.Value = null;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}
