using System.Buffers.Binary;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Tencent.IM;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Player chat. The client's chat is Tencent IM only; the patched client (Ro3Emu.ClientHooks
/// ServerImAdapter) tunnels the Tencent.IM protobuf commands through game messages 77010 (up) / 77011 (down),
/// frame = uint32 LE cmd ‖ int32 LE errorCode ‖ body. Groups are the client's opaque conversation ids
/// ("4_&lt;sceneId&gt;" map chat, "@TGS#_1_&lt;cluster&gt;" world, ...), joined through romsg 77002; whispers address
/// a role id.
/// </summary>
public sealed partial class ChatService
{
    private readonly GmCommands _gm;
    private readonly FeedbackService _feedback;
    private readonly GuildStore _guilds;
    private readonly FriendStore _friends;
    private readonly CharacterService _character;
    private readonly GameData _data;
    private readonly SessionRegistry _sessions;
    private readonly IOptions<ServerOptions> _options;
    private readonly ILogger<ChatService> _log;

    public ChatService(GmCommands gm, FeedbackService feedback, GuildStore guilds, FriendStore friends, CharacterService character,
        GameData data, SessionRegistry sessions, IOptions<ServerOptions> options, ILogger<ChatService> log)
    {
        this._gm = gm; this._feedback = feedback; this._guilds = guilds; this._friends = friends; this._character = character; this._data = data;
        this._sessions = sessions; this._options = options; this._log = log;
        character.RegisterItemAction(SoundTransmissionAction, UseHornAsync, takesParam: true);
    }

    /// <summary>Bug reports: "/bug" lines here and the client hook's message (GameMessageHandler routes it through this).</summary>
    public FeedbackService Feedback => _feedback;

    /// <summary>Player chat commands ("/pass week"): the first word, any case, and the rest of the line. Never shown to others.</summary>
    public Dictionary<string, Func<GameSession, string, Task>> PlayerCommands { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const uint ImUserSig = 77001;
    public const uint JoinChannel = 77002;
    public const uint TunnelUp = 77010;
    public const uint TunnelDown = 77011;
    public const uint FunctionOpenSync = 5056;

    /// <summary>LauncherConfig im_app_id of this build (the client-side check is patched out anyway).</summary>
    private const int SdkAppId = 20047690;

    /// <summary>FunctionConfig ids of the chat channels 1..11 (ChatChannelConfig _iType = 2400 + channel).</summary>
    private static readonly long[] ChatFunctions = Enumerable.Range(2401, 11).Select(i => (long)i).ToArray();

    /// <summary>
    /// Character UI (enum_FunctionType): 26 Bag with 2601-2603 (2603 EquipInfo = the equipment window), 15 Card with
    /// 1501-1504, 28 Emblem, 29 Totem, 22 PlayerInfo with 2201-2203 (attributes, add points), 11 SetMenu, 14 Title,
    /// 21 Achievements, 32 Wardrobe with 3201-3210, 45 Friend with 4501-4505, 57 Map, 97 Menu.
    /// </summary>
    private static readonly long[] BagFunctions =
    [
        26, 2601, 2602, 2603, 15, 1501, 1502, 1503, 1504, 28, 29, 22, 2201, 2202, 2203, 11, 14, 21,
        32, .. Enumerable.Range(3201, 10).Select(i => (long)i), 45, 4501, 4502, 4503, 4504, 4505, 57, 97,
    ];

    private enum Cmd : uint
    {
        JoinChannel = 0, QuitChannel = 1, GetJoinedChannel = 2, SendText = 3, NotifyNewText = 4,
        GetHistory = 11, DeleteHistory = 12,
    }

    private const uint ChatTypePrivate = 1;

    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<GameSession>> _groups = new();
    private readonly Dictionary<long, GameSession> _online = new();
    private long _nextMessageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Server-origin recruitment uses the client's channel-8 Tencent text push. The special
    /// extra-param template 100/101 and nested JSON data are parsed by LC_ChatMessageHandler.lua:802-849;
    /// channel ids are P + "8_" + cluster_id. No IM SendText ACK is emitted.
    /// </summary>
    public async Task PublishRecruitmentAsync(GameSession sender, string content, long guildId = 0,
        bool preCreate = false, int helperCount = 0)
    {
        List<(string Id, GameSession[] Recipients)> channels;
        lock (_sync)
            channels = _groups.Where(x => x.Key.StartsWith("8_", StringComparison.Ordinal)
                    || x.Key.StartsWith("@TGS#_8_", StringComparison.Ordinal))
                .Select(x => (x.Key, x.Value.ToArray())).ToList();
        string extra = guildId == 0 ? "" : JsonSerializer.Serialize(new
        {
            templateId = preCreate ? 100 : 101,
            commonData = preCreate
                ? JsonSerializer.Serialize(new { guildId, helperCount })
                : JsonSerializer.Serialize(new { guildId, playerId = sender.Role!.Id.ToString() }),
        });
        foreach (var (id, recipients) in channels)
        {
            var message = new TextChatMessage
            {
                MessageId = Interlocked.Increment(ref _nextMessageId).ToString(),
                SenderId = sender.Role!.Id.ToString(), ReceiveId = id,
                SendTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Content = content, ExtraParam = extra,
            };
            Remember(message);
            var push = new NotifyNewTextChatMessage { Msg = message };
            foreach (var to in recipients)
            {
                try { await DownAsync(to, Cmd.NotifyNewText, push); }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
            }
        }
    }

    /// <summary>After role select: open the chat functions (else every channel is inactive) and register for whispers.</summary>
    public async Task OnRoleEnteredAsync(GameSession session)
    {
        lock (_sync) _online[session.Role!.Id] = session;
        await Task.CompletedTask; // functions (chat channels included) are opened by FunctionService
    }

    public void OnRoleLeft(GameSession session)
    {
        lock (_sync)
        {
            if (session.Role != null && _online.TryGetValue(session.Role.Id, out var s) && s == session) _online.Remove(session.Role.Id);
            foreach (var group in session.ChatGroups) RemoveMember(group, session);
            session.ChatGroups.Clear();
            _sendBudget.Remove(session);
        }
    }

    public Task UserSigAsync(GameSession session) =>
        session.SendGameAsync(ImUserSig, new IMSDKUserSigRsp
        {
            Result = 0,
            UserSig = "ro3emu",
            SdkAppID = SdkAppId,
            ExpireTime = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        });

    /// <summary>
    /// Join, then replay the group's recent lines as ordinary pushes (the client's history request must stay empty:
    /// a non-empty text list without an audio list breaks its Lua).
    /// </summary>
    public async Task JoinAsync(GameSession session, JoinChatChannelReq req)
    {
        bool tooMany;
        lock (_sync) tooMany = session.ChatGroups.Count >= MaxJoinedGroups && !session.ChatGroups.Contains(req.GroupId);
        if (req.GroupId.Length > MaxReceiveIdChars || tooMany)
        {
            _log.LogWarning("Chat join of {Name} refused: {Length}-character id, {Joined} channels joined",
                session.Role?.Name, req.GroupId.Length, session.ChatGroups.Count);
            await session.SendGameAsync(JoinChannel, new JoinChatChannelRsp { Result = ErrorCodes.Parameter, Req = req });
            return;
        }
        if (!GuildAllowed(session, req.GroupId))
        {
            await session.SendGameAsync(JoinChannel, new JoinChatChannelRsp { Result = ErrorCodes.NotInGuild, Req = req });
            return;
        }
        bool newlyJoined = Join(session, req.GroupId);
        await session.SendGameAsync(JoinChannel, new JoinChatChannelRsp { Result = 0, Req = req });
        // Repeated subscriptions still need their ACK, but replaying history would duplicate
        // lines already delivered to this session. Leaving/rejoining starts a fresh subscription.
        if (!newlyJoined) return;
        List<TextChatMessage> recent;
        lock (_sync) recent = _recent.TryGetValue(HistoryKey(req.GroupId, session), out var history) ? history.Lines.ToList() : new();
        long runtime = session.WorldScene, generation = session.SceneGeneration;
        foreach (var line in recent)
        {
            if (MapChannel(req.GroupId) && (session.SceneLoading || session.Handoff != null
                || session.WorldScene != runtime || session.SceneGeneration != generation)) break;
            await DownAsync(session, Cmd.NotifyNewText, new NotifyNewTextChatMessage { Msg = line });
        }
    }

    private const int RecentLines = 20;
    private static bool GuildChannel(string group) => group.StartsWith("2_", StringComparison.Ordinal);

    /// <summary>Tavern membership of chat channel 10 (TavernService.InRoom), set by ServerHost.</summary>
    public Func<long, long, bool>? TavernMember { get; set; }

    /// <summary>EChannelBarRoom (10): "10_&lt;room&gt;", optionally with Tencent's "@TGS#_" prefix (LC_ChatHelper).</summary>
    private static bool TavernChannel(string group, out long room)
    {
        room = 0;
        string rest = group.StartsWith("@TGS#_", StringComparison.Ordinal) ? group[6..] : group;
        return rest.StartsWith("10_", StringComparison.Ordinal) && long.TryParse(rest.AsSpan(3), out room) && room > 0
            && rest == $"10_{room}";
    }

    /// <summary>A role left a Tavern room: its chat channel stops reaching it.</summary>
    public void TavernLeft(GameSession session, long room)
    {
        Leave(session, $"10_{room}");
        Leave(session, $"@TGS#_10_{room}");
    }

    private bool GuildAllowed(GameSession session, string group)
    {
        if (group.StartsWith("10_", StringComparison.Ordinal) || group.StartsWith("@TGS#_10_", StringComparison.Ordinal))
            return TavernChannel(group, out long room) && session.Role != null && TavernMember?.Invoke(session.Role.Id, room) == true;
        if (group.StartsWith("5_", StringComparison.Ordinal))
            return long.TryParse(group.AsSpan(2), out long groupId) && groupId > 0 && group == $"5_{groupId}"
                && session.Role != null && _friends.Group(groupId.ToString()) is { } chatGroup
                && chatGroup.Members.Any(m => m.Role == session.Role.Id);
        if (!GuildChannel(group)) return true;
        // Canonical spelling is required so leaving 2_<id> always removes the exact joined group.
        return long.TryParse(group.AsSpan(2), out long guildId) && guildId > 0
            && group == $"2_{guildId}" && session.Role != null && _guilds.ForRole(session.Role.Id)?.Id == guildId;
    }

    public void GuildMembershipChanged(GameSession session, long formerGuildId) => Leave(session, $"2_{formerGuildId}");
    /// <summary>Friend group removal revokes an already joined IM channel before the next message.</summary>
    public void FriendMembershipChanged(GameSession session, string formerGroupId) => Leave(session, $"5_{formerGroupId}");

    /// <summary>
    /// Recent lines per channel, replayed on join. Any client can name a new channel id, so the store is bounded as a whole:
    /// past <see cref="MaxRecentGroups"/> channels or <see cref="MaxRecentChars"/> stored characters (~8 MB of UTF-16), the
    /// channels written to least recently lose their history first.
    /// </summary>
    private readonly Dictionary<(string Group, long Runtime), History> _recent = new();
    private readonly LinkedList<History> _recentOrder = new(); // least recently written first
    private long _recentChars;
    internal const int MaxRecentGroups = 2048, MaxRecentChars = 4_000_000;

    private sealed class History((string Group, long Runtime) key)
    {
        public (string Group, long Runtime) Key { get; } = key;
        public Queue<TextChatMessage> Lines { get; } = new();
        public long Chars;
        public LinkedListNode<History>? Node;
    }

    private static int Size(TextChatMessage line) =>
        line.Content.Length + line.ExtraParam.Length + line.ReceiveId.Length + line.SenderId.Length + line.MessageId.Length;

    private void DropHistory(History history)
    {
        _recent.Remove(history.Key);
        _recentOrder.Remove(history.Node!);
        _recentChars -= history.Chars;
    }

    /// <summary>Channels with history (tests).</summary>
    internal int RecentGroupCount { get { lock (_sync) return _recent.Count; } }
    /// <summary>Channels with at least one joined session (tests).</summary>
    internal int GroupCount { get { lock (_sync) return _groups.Count; } }

    // LC_ChatHelper:5194-5225 uses runtime scene id, optionally with Tencent's prefix.
    // Scope legacy/shared channel spellings too; membership is never permission to listen across lines.
    private static bool MapChannel(string group) => group.StartsWith("4_", StringComparison.Ordinal)
        || group.StartsWith("@TGS#_4_", StringComparison.Ordinal);
    private static (string Group, long Runtime) HistoryKey(string group, GameSession session) =>
        (group, MapChannel(group) ? session.WorldScene : 0);

    public Task RetireLineAsync(Maps.RuntimeMapKey map)
    {
        lock (_sync)
        {
            foreach (var history in _recent.Values.Where(h => h.Key.Runtime == map.WorldScene).ToArray()) DropHistory(history);
            foreach (string group in new[] { $"4_{map.WorldScene}", $"@TGS#_4_{map.WorldScene}" })
                if (_groups.Remove(group, out var members))
                    foreach (var member in members) member.ChatGroups.Remove(group);
        }
        return Task.CompletedTask;
    }

    private void Remember(TextChatMessage message, GameSession? sender = null)
    {
        lock (_sync)
        {
            var key = sender == null ? (message.ReceiveId, 0L) : HistoryKey(message.ReceiveId, sender);
            if (!_recent.TryGetValue(key, out var history))
            {
                _recent[key] = history = new History(key);
                history.Node = _recentOrder.AddLast(history);
            }
            else
            {
                _recentOrder.Remove(history.Node!);
                _recentOrder.AddLast(history.Node!);
            }
            history.Lines.Enqueue(message);
            int size = Size(message);
            history.Chars += size;
            _recentChars += size;
            while (history.Lines.Count > RecentLines)
            {
                int old = Size(history.Lines.Dequeue());
                history.Chars -= old;
                _recentChars -= old;
            }
            while ((_recent.Count > MaxRecentGroups || _recentChars > MaxRecentChars) && _recentOrder.First!.Value != history)
                DropHistory(_recentOrder.First.Value);
        }
    }

    internal static bool IsEventOwnerRequest(uint message, ReadOnlyMemory<byte> frame)
    {
        if (message != TunnelUp || frame.Length < 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame.Span) != (uint)Cmd.SendText) return false;
        try { return GmCommands.IsEventOwnerCommand(SendTextChatMessageRequest.Parser.ParseFrom(frame.Span[8..]).Content.Trim()); }
        catch (InvalidProtocolBufferException) { return false; }
    }

    public async Task TunnelAsync(GameSession session, ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 8 || session.Role == null) return;
        var cmd = (Cmd)BinaryPrimitives.ReadUInt32LittleEndian(frame.Span);
        var body = frame[8..];
        switch (cmd)
        {
            case Cmd.QuitChannel:
                var quit = QuitChannelRequest.Parser.ParseFrom(body.Span);
                Leave(session, quit.ChannelId);
                await DownAsync(session, cmd, new QuitChannelResponse { ChatResult = true, ChannelId = quit.ChannelId });
                break;

            case Cmd.GetJoinedChannel:
                var joined = new GetJoinedChannelResponse { ChatResult = true };
                lock (_sync) joined.ChannelList.AddRange(session.ChatGroups);
                await DownAsync(session, cmd, joined);
                break;

            case Cmd.SendText:
                await SendTextAsync(session, SendTextChatMessageRequest.Parser.ParseFrom(body.Span));
                break;

            case Cmd.GetHistory:
                // No history kept: answer with both lists absent (one present and one absent makes the Lua index nil).
                var history = GetHistoryMessageRequest.Parser.ParseFrom(body.Span);
                await DownAsync(session, cmd, new GetHistoryMessageResponse { ChatResult = true, TargetId = history.TargetId, ChatType = history.ChatType });
                break;

            case Cmd.DeleteHistory:
                var delete = DeleteHistoryMessageRequest.Parser.ParseFrom(body.Span);
                await DownAsync(session, cmd, new DeleteHistoryMessageResponse { ChatResult = true, ChatType = delete.ChatType });
                break;

            default:
                // Join via IM (unused), voice messages, translation...: chat_result false (field absent = false).
                _log.LogDebug("chat cmd {Cmd} from {Name} refused", (uint)cmd, session.Role.Name);
                await DownRawAsync(session, (uint)cmd, 0, []);
                break;
        }
    }

    /// <summary>
    /// Upper bounds of one chat line. The client's input box and link cards stay far below them; a modified client could
    /// otherwise push a megabyte line to every member of a channel and into the 20-line history of each group id.
    /// </summary>
    internal const int MaxContentChars = 1024, MaxExtraParamChars = 8192, MaxReceiveIdChars = 128;

    private async Task SendTextAsync(GameSession sender, SendTextChatMessageRequest req)
    {
        if (req.Content.Length > MaxContentChars || req.ExtraParam.Length > MaxExtraParamChars || req.ReceiveId.Length > MaxReceiveIdChars)
        {
            _log.LogWarning("Chat from {Name} refused: {Content} content, {Extra} extra and {Receive} receiver characters",
                sender.Role!.Name, req.Content.Length, req.ExtraParam.Length, req.ReceiveId.Length);
            await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = false });
            return;
        }
        if (!GuildAllowed(sender, req.ReceiveId))
        {
            await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = false });
            return;
        }
        var message = new TextChatMessage
        {
            MessageId = Interlocked.Increment(ref _nextMessageId).ToString(),
            SenderId = sender.Role!.Id.ToString(),
            ReceiveId = req.ReceiveId,
            SendTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Content = req.Content,
            ExtraParam = req.ExtraParam,
        };

        if (FeedbackService.TryParseChatCommand(req.Content, out var report))
        {
            // Bug report: acknowledged like a GM line and shown to nobody; the answer comes on the marquee.
            await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = true, Msg = message });
            await _feedback.SubmitAsync(sender, report, "chat");
            return;
        }

        if (PlayerCommand(req.Content) is var (command, args))
        {
            await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = true, Msg = message });
            await command(sender, args);
            return;
        }

        if (_gm.IsCommand(req.Content))
        {
            // GM command: acknowledge the send (the client waits for it) but show the line to nobody.
            await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = true, Msg = message });
            await _gm.RunAsync(sender, req.Content.Trim());
            return;
        }

        if (!TakeSendBudget(sender))
        {
            _log.LogWarning("Chat from {Name} refused: over {Rate} lines/s", sender.Role.Name, SendLinesPerSecond);
            await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = false });
            return;
        }

        List<GameSession> recipients;
        bool local = req.ChatType != ChatTypePrivate && MapChannel(req.ReceiveId);
        long runtime = sender.WorldScene, generation = sender.SceneGeneration;
        if (local && (sender.SceneLoading || sender.Handoff != null))
        {
            await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = false });
            return;
        }
        lock (_sync)
        {
            if (req.ChatType == ChatTypePrivate)
            {
                recipients = new() { sender };
                if (long.TryParse(req.ReceiveId, out long target) && _online.TryGetValue(target, out var to) && to != sender) recipients.Add(to);
            }
            else
            {
                recipients = _groups.TryGetValue(req.ReceiveId, out var members) ? members.ToList() : new();
                if (MapChannel(req.ReceiveId))
                    recipients = recipients.Where(p => !p.SceneLoading && p.WorldScene == sender.WorldScene).ToList();
                if (!recipients.Contains(sender)) recipients.Add(sender);
            }
        }
        if (req.ChatType != ChatTypePrivate && req.ReceiveId.StartsWith("5_", StringComparison.Ordinal))
        {
            if (!GuildAllowed(sender, req.ReceiveId))
            {
                await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = false });
                return;
            }
            recipients = recipients.Where(p => GuildAllowed(p, req.ReceiveId)).ToList();
        }
        if (req.ChatType != ChatTypePrivate) Remember(message, sender);
        _log.LogInformation("Chat {Name} -> {Target} ({Count} recipient(s)): {Text}", sender.Role.Name, req.ReceiveId, recipients.Count, req.Content);

        await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = true, Msg = message });
        // Every recipient gets the push, the sender included: the send response alone does not add the line.
        var push = new NotifyNewTextChatMessage { Msg = message };
        foreach (var to in recipients)
        {
            // Sends yield; neither a moved recipient nor a superseded sender may leak an old line's push.
            if (local && (sender.SceneGeneration != generation || sender.Handoff != null
                || to.SceneLoading || to.Handoff != null || to.WorldScene != runtime)) continue;
            try { await DownAsync(to, Cmd.NotifyNewText, push); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// 42034 recruitment: publish the client's special chat element on every joined recruit channel.
    /// LC_ChatMessageHandler.lua:780-803 decodes extra_param.templateId=1 and commonData with teamId/senderRoleId;
    /// LC_DAL_Team.lua:3191-3282 subsequently resolves the link through PartyService.getParty.
    /// </summary>
    private (Func<GameSession, string, Task> Run, string Args)? PlayerCommand(string? content)
    {
        var line = (content ?? "").TrimStart();
        if (!line.StartsWith('/')) return null;
        int space = line.IndexOfAny([' ', '	']);
        string word = space < 0 ? line : line[..space];
        return PlayerCommands.TryGetValue(word, out var run) ? (run, space < 0 ? "" : line[(space + 1)..].Trim()) : null;
    }

    public async Task PublishTeamRecruitmentAsync(GameSession sender, string content, long teamId, int current,
        int capacity, IReadOnlyList<int> jobs)
    {
        var extra = JsonSerializer.Serialize(new
        {
            templateId = 1,
            commonData = JsonSerializer.Serialize(new
            {
                teamId = teamId.ToString(), senderRoleId = sender.Role!.Id.ToString(), descriptions = content,
                currNum = current, maxNum = capacity, teamMember = jobs,
            }),
        });
        List<(string Id, GameSession[] Members)> channels;
        lock (_sync) channels = _groups.Where(p => p.Key.StartsWith("@TGS#_8_", StringComparison.Ordinal) || p.Key.StartsWith("8_", StringComparison.Ordinal))
            .Select(p => (p.Key, p.Value.ToArray())).ToList();
        foreach (var (id, recipients) in channels)
        {
            var message = new TextChatMessage
            {
                MessageId = Interlocked.Increment(ref _nextMessageId).ToString(), SenderId = sender.Role.Id.ToString(),
                ReceiveId = id, SendTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Content = content, ExtraParam = extra,
            };
            Remember(message);
            foreach (var target in recipients)
                try { await DownAsync(target, Cmd.NotifyNewText, new NotifyNewTextChatMessage { Msg = message }); }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// The Guild League elite team card in the guild channel "2_&lt;guild&gt;": extra_param templateId 102 GuildTeam with
    /// commonData guildId/teamId/playerId (LC_ChatMessageHandler.lua:850-870; the client shows text 170138 and the
    /// LC_LinkHelper GuildTeam link, whose tap sends 42014 for teamId).
    /// </summary>
    public async Task PublishGuildTeamAsync(GameSession sender, long guildId, long teamId, string content)
    {
        string group = $"2_{guildId}";
        GameSession[] recipients;
        lock (_sync) recipients = _groups.TryGetValue(group, out var members) ? members.ToArray() : [];
        var message = new TextChatMessage
        {
            MessageId = Interlocked.Increment(ref _nextMessageId).ToString(), SenderId = sender.Role!.Id.ToString(),
            ReceiveId = group, SendTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Content = content,
            ExtraParam = JsonSerializer.Serialize(new
            {
                templateId = 102,
                commonData = JsonSerializer.Serialize(new { guildId, teamId = teamId.ToString(), playerId = sender.Role.Id.ToString() }),
            }),
        };
        Remember(message);
        foreach (var to in recipients.Where(r => GuildAllowed(r, group)))
            try { await DownAsync(to, Cmd.NotifyNewText, new NotifyNewTextChatMessage { Msg = message }); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Lines a session may show to others: a burst of <see cref="SendBurst"/>, refilled at <see cref="SendLinesPerSecond"/>.
    /// Far above anyone typing; it stops a modified client flooding channels. GM, player and /bug commands are not counted.
    /// </summary>
    internal const int SendBurst = 20, SendLinesPerSecond = 5;
    private readonly Dictionary<GameSession, (double Tokens, long Stamp)> _sendBudget = new();
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private bool TakeSendBudget(GameSession session)
    {
        lock (_sync)
        {
            long now = Clock();
            double tokens = _sendBudget.TryGetValue(session, out var budget)
                ? Math.Min(SendBurst, budget.Tokens + (now - budget.Stamp) * SendLinesPerSecond / 1000.0)
                : SendBurst;
            if (tokens < 1)
            {
                _sendBudget[session] = (tokens, now);
                return false;
            }
            _sendBudget[session] = (tokens - 1, now);
            return true;
        }
    }

    /// <summary>Channels one session may be in at once; the client joins about a dozen.</summary>
    internal const int MaxJoinedGroups = 128;

    private void RemoveMember(string group, GameSession session)
    {
        if (_groups.TryGetValue(group, out var members) && members.Remove(session) && members.Count == 0) _groups.Remove(group);
    }

    private bool Join(GameSession session, string group)
    {
        lock (_sync)
        {
            if (!_groups.TryGetValue(group, out var members)) _groups[group] = members = new();
            bool added = members.Add(session);
            session.ChatGroups.Add(group);
            return added;
        }
    }

    private void Leave(GameSession session, string group)
    {
        lock (_sync)
        {
            RemoveMember(group, session);
            session.ChatGroups.Remove(group);
        }
    }

    private Task DownAsync(GameSession session, Cmd cmd, IMessage body) => DownRawAsync(session, (uint)cmd, 0, body.ToByteArray());

    private static Task DownRawAsync(GameSession session, uint cmd, int error, byte[] body)
    {
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, cmd);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), error);
        body.CopyTo(frame, 8);
        return session.SendGameRawAsync(TunnelDown, frame);
    }
}
