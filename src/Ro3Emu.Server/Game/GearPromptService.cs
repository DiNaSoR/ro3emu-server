using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The client's bottom-right "upgrade, Equip" prompt (UI_Com_UseWidget) is fed only by 31322 GetRewardNotify:
/// LC_DAL_Reward._onGetRewardNotify -> LC_TipHelper.PopRewardTip -> LC_FastUseManager.AddFastUse ->
/// LC_EquipmentHelper.CanFastEquip, which reads the reward row itself (itemGuid, itemInfos[3] appraised, itemInfos[2]
/// score against the worn piece). This is the one place that makes every way gear reaches the bag produce such a row,
/// instead of each grant path doing it:
/// <list type="bullet">
/// <item>Every 5009 bag sync is watched: an equipment GUID the session has not seen before, sitting in its bag pack,
/// is new gear (craft, mail, shop, trade, auction, box, GM give, any award).</item>
/// <item>Every outgoing 31322 is watched: an equipment row that already names a GUID counts as shown; a count-only row
/// (itemGuid 0) is replaced by rows for the new instances of that entry.</item>
/// <item>New gear still unshown shortly after its sync (the path sent no 31322, e.g. 31404 crafting) gets a 31322 of
/// its own, ManifestTypeFloat (1): the "obtained" floating text, which also adds the prompt at once
/// (PopRewardTip caches only Common/Equipment/Newer). Using Float for these is (inferred).</item>
/// </list>
/// GUIDs already in the bag at login, unequipped gear and gear moved between packs keep their GUIDs, so they never
/// count as new. Only appraised gear gets a row of its own: CanFastEquip refuses the rest.
/// </summary>
public sealed class GearPromptService(CharacterService character, GameData data, GameLoop loop)
{
    /// <summary>Long enough for a grant's own 5009 + 31322 pair to go out first.</summary>
    internal static TimeSpan FlushDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    private const int ManifestFloat = 1, ItemTypeEquip = 5;

    private readonly CharacterService _character = character;
    private readonly GameData _data = data;
    private readonly GameLoop _loop = loop;

    private sealed class State
    {
        internal readonly HashSet<long> Known = new();
        internal readonly HashSet<long> Shown = new();
        internal readonly Dictionary<long, InvItem> Pending = new();
        internal bool FlushScheduled;
    }

    /// <summary>After the bag is loaded: what is there now is not new.</summary>
    internal void Attach(GameSession session)
    {
        var state = new State();
        foreach (var item in session.Items) if (item.Guid > 0) state.Known.Add(item.Guid);
        session.BeforeGameSend = (msgId, body) => Observe(session, state, msgId, body);
    }

    private bool IsEquipment(int entry) => _data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeEquip;

    private bool InBag(InvItem item) => item.Grid == 0 && item.Guid > 0
        && _data.Row("ItemConfig", item.Entry) is { } row && item.Pack == CharacterService.ConfigPack(row);

    private void Observe(GameSession session, State state, uint msgId, IMessage body)
    {
        if (msgId == CharacterService.ItemsSync) SeeBag(session, state);
        else if (msgId == CharacterService.GetRewardNotify && body is GetRewardNotify notify) Fill(session, state, notify);
    }

    private void SeeBag(GameSession session, State state)
    {
        bool added = false;
        foreach (var item in session.Items)
        {
            if (item.Guid <= 0 || !state.Known.Add(item.Guid)) continue;
            if (!IsEquipment(item.Entry) || !InBag(item) || state.Shown.Contains(item.Guid)) continue;
            state.Pending[item.Guid] = item;
            added = true;
        }
        if (!added || state.FlushScheduled) return;
        state.FlushScheduled = true;
        long role = session.Role?.Id ?? 0;
        var attachment = session.Attachment;
        _loop.Schedule(FlushDelay, async () =>
        {
            state.FlushScheduled = false;
            if (role == 0 || session.Role?.Id != role || !ReferenceEquals(session.Attachment, attachment)
                || attachment?.Cancelled == true) return;
            await FlushAsync(session, state);
        });
    }

    /// <summary>New, still unshown instances of an entry: pending first, then any not yet seen in a 5009.</summary>
    private IEnumerable<InvItem> Fresh(GameSession session, State state, int entry, int bind) =>
        session.Items.Where(i => i.Entry == entry && i.BindType == bind && InBag(i) && !state.Shown.Contains(i.Guid)
                && (state.Pending.ContainsKey(i.Guid) || !state.Known.Contains(i.Guid)))
            .OrderByDescending(i => i.Guid);

    private void Fill(GameSession session, State state, GetRewardNotify notify)
    {
        for (int r = 0; r < notify.Rewards.Count; r++)
        {
            var row = notify.Rewards[r];
            if (!IsEquipment(row.ItemEntry)) continue;
            if (row.ItemGuid > 0) { Mark(state, row.ItemGuid); continue; }
            int bind = (int)(row.ItemBindInfo?.ItemBindType ?? 0);
            var fresh = Fresh(session, state, row.ItemEntry, bind).Take(Math.Max(1, row.Stack)).ToList();
            if (fresh.Count == 0) continue;
            notify.Rewards.RemoveAt(r);
            int left = row.Stack - fresh.Count;
            foreach (var item in fresh)
            {
                var shown = _character.ItemInfoOf(item);
                // A drop notice row carries the set-up source key the dropped-item flight reads.
                notify.Rewards.Insert(r++, shown);
                Mark(state, item.Guid);
            }
            if (left > 0) { row.Stack = left; notify.Rewards.Insert(r++, row); }
            r--;
        }
    }

    private static void Mark(State state, long guid)
    {
        state.Shown.Add(guid);
        state.Known.Add(guid);
        state.Pending.Remove(guid);
    }

    private async Task FlushAsync(GameSession session, State state)
    {
        var rows = state.Pending.Values
            .Where(i => session.Items.Contains(i) && InBag(i) && CharacterService.IsAppraised(i) && !state.Shown.Contains(i.Guid))
            .OrderBy(i => i.Guid).ToList();
        state.Pending.Clear();
        if (rows.Count == 0 || session.Role is not { } role) return;
        var notify = new GetRewardNotify { TargetUid = role.Id, RewardType = { ManifestFloat } };
        foreach (var item in rows) notify.Rewards.Add(_character.ItemInfoOf(item));
        await session.SendGameAsync(CharacterService.GetRewardNotify, notify);
    }
}
