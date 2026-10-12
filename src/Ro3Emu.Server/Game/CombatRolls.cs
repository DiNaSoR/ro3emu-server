namespace Ro3Emu.Server.Game;

/// <summary>Uniform rolls used by <see cref="CombatService"/>; game-loop only, like the service.</summary>
internal class CombatRolls
{
    private readonly Random _random = new();
    /// <summary>[0, 1); a roll below the hit rate hits.</summary>
    public virtual double Hit() => _random.NextDouble();
    /// <summary>[0, 1) across DamageFloat [9700, 10300].</summary>
    public virtual double DamageFloat() => _random.NextDouble();
    /// <summary>[0, 1); a roll below the crit chance crits.</summary>
    public virtual double Crit() => _random.NextDouble();
    /// <summary>[0, max) for per-mille style procs.</summary>
    public virtual int Proc(int max) => _random.Next(max);
}
