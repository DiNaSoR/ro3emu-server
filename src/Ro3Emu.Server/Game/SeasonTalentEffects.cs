namespace Ro3Emu.Server.Game;

/// <summary>
/// Season talent job nodes' SpecialEffectConfig tuples beyond attributes (those are SeasonTalentService.Attributes').
/// Opcode 2 names a zero-time BuffConfig row (160 of the export's job node tuples, all _iTime 0): it is registered as a
/// passive owned by its talent node for as long as the node applies, so its _kSkillAttributeList reaches
/// SkillParameterRules like any live buff ("Basic Attack's percentage damage multiplier increases by 3%" is
/// 1522201's [0,10050,29,1,300] rows). Opcode 21 is [21, weapon subtype or 0, scope, selector, type, operation,
/// value, index]: after the weapon slot it is BuffConfig's skill attribute tuple ("Bowling Bash's Cooldown is changed
/// to 8 seconds" is [21,0,0,11104,0,2,8000]), so it joins the same rules as a non-buff modifier (inferred reading,
/// checked against each row's description). Its scope-1 tag selectors do not occur and are not decoded.
/// Not executed: buff procs in _kLogicData (no generic proc engine), opcode 12 (another buff's parameters) and 20.
/// </summary>
public sealed class SeasonTalentEffects
{
    private readonly SeasonTalentService _talents;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly CombatService _combat;
    private readonly SkillService _skills;
    private readonly Data.GameData _data;

    public SeasonTalentEffects(Data.GameData data, SeasonTalentService talents, CharacterService character, BuffService buffs,
        CombatService combat, SkillService skills, SceneService scenes)
    {
        this._data = data;
        this._talents = talents;
        this._character = character;
        this._buffs = buffs;
        this._combat = combat;
        this._skills = skills;
        talents.Changed.Add(RefreshAsync);
        scenes.Loaded.Add(RefreshAsync); // a scene change clears _iSceneClear passives
        combat.PlayerRevived.Add(RefreshAsync); // and death clears _iDeadClear ones
        character.EquipmentChanged.Add(RefreshAsync); // weapon-conditioned opcode 21
        character.JobChanged.Add(RefreshAsync); // job nodes have per-job rows
    }

    /// <summary>The talent-owned passives the session should hold: (talent node, BuffConfig id).</summary>
    internal HashSet<(long Node, int Buff)> WantedPassives(GameSession s) => _talents.SpecialEffects(s)
        .Where(e => e.Op is [2, var buff] && _data.Row("BuffConfig", buff) != null)
        .Select(e => (e.Node, (int)e.Op[1])).ToHashSet();

    internal List<SkillParameterRules.Modifier> WantedModifiers(GameSession s)
    {
        var result = new List<SkillParameterRules.Modifier>();
        int? weapon = null;
        foreach (var (_, op) in _talents.SpecialEffects(s))
        {
            if (op.Length < 7 || op[0] != 21 || op[2] != 0 || op[3] <= 0) continue;
            if (op[1] != 0 && (weapon ??= _skills.Weapon(s).SubType) != op[1]) continue;
            result.Add(new SkillParameterRules.Modifier((int)op[3], (int)op[4], (int)op[5], op[6], op.Length > 7 ? (int)op[7] : 0));
        }
        return result;
    }

    /// <summary>Reconcile talent-owned passives and skill modifiers with the effective talents.</summary>
    public async Task RefreshAsync(GameSession s)
    {
        if (s.Role == null) return;
        var wanted = WantedPassives(s);
        bool statsChanged = false, changed = false;
        foreach (var stale in s.Buffs.Where(b => b.IsPassive && b.IsTalentOwned && !wanted.Contains((b.SourceId, b.ConfigId))).ToArray())
        {
            statsChanged |= stale.Changes.Count > 0;
            changed = true;
            await _buffs.RemoveAsync(s, stale);
        }
        foreach (var (node, id) in wanted)
        {
            if (s.Buffs.Any(b => b.IsPassive && b.IsTalentOwned && b.SourceId == node && b.ConfigId == id)) continue;
            await _buffs.AddPassiveAsync(s, id, node, totemOwned: false, talentOwned: true);
            changed = true;
            statsChanged |= s.Buffs.FirstOrDefault(b => b.IsPassive && b.IsTalentOwned && b.SourceId == node && b.ConfigId == id)
                is { Changes.Count: > 0 };
        }
        var modifiers = WantedModifiers(s);
        if (!modifiers.SequenceEqual(s.ExtraSkillModifiers))
        {
            s.ExtraSkillModifiers = modifiers;
            s.ExtraSkillModifierVersion++;
            changed = true;
        }
        if (statsChanged)
        {
            if (s.SceneLoading) _character.Refresh(s);
            else await _character.RefreshStatsAsync(s);
        }
        // AddPassiveAsync does not run BuffService.Changed, so publish the skill parameters (31132) here.
        if (changed && !s.SceneLoading) await _combat.RefreshSkillParametersAsync(s);
    }
}
