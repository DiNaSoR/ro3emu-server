using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The room requests 82481-82514. Each answers its sender; a nonzero result is an ErrorCodeConfig code (2551-2580) the
/// client shows as a tip. Room state changes are published with <see cref="PublishAsync"/>.
/// </summary>
public sealed partial class TavernService
{
    // ------------------------------------------------------------------ joining and leaving

    /// <summary>82481: opens a room owned by the sender (any room the sender was in is left first).</summary>
    public async Task CreateAsync(GameSession s, CreateBarRoomReq req)
    {
        long self = s.Role!.Id;
        int code = CheckInfo(req.BarRoomType, req.LanguageType, req.BarRoomName);
        if (code != 0) { await s.SendGameAsync(Create, new CreateBarRoomResp { Result = code }); return; }
        if (_byMember.TryGetValue(self, out var old)) await LeaveRoomAsync(s, old, notifySelf: false);
        long now = Now;
        var room = new Room
        {
            Id = _nextId++, Type = req.BarRoomType, Language = req.LanguageType, Name = req.BarRoomName.Trim(),
            Region = _options.Value.ClusterId, Owner = self, Created = now,
        };
        _rooms[room.Id] = room;
        Join(s, room, req.AreaId, now);
        await s.SendGameAsync(Create, new CreateBarRoomResp { Result = 0, Info = Detail(room) });
        await SyncAsync(s);
        _log.LogInformation("Role {Role} opened tavern room {Room} \"{Name}\"", self, room.Id, room.Name);
    }

    /// <summary>82482: joins a room from the list (or a random Discover pick).</summary>
    public async Task EnterAsync(GameSession s, EnterBarRoomReq req)
    {
        long self = s.Role!.Id;
        if (RoomById(req.BarRoomGuid) is not { } room) { await s.SendGameAsync(Enter, new EnterBarRoomResp { Result = ErrNoRoom }); return; }
        if (room.Has(self)) { await s.SendGameAsync(Enter, new EnterBarRoomResp { Result = 0, Info = Detail(room) }); return; }
        int code = CanJoin(room, self);
        if (code != 0) { await s.SendGameAsync(Enter, new EnterBarRoomResp { Result = code }); return; }
        await JoinAndAnnounceAsync(s, room, req.AreaId);
        await s.SendGameAsync(Enter, new EnterBarRoomResp { Result = 0, Info = Detail(room) });
    }

    private int CanJoin(Room room, long role) =>
        room.Blocked.Contains(role) ? ErrBlocked : room.Members.Count >= Capacity ? ErrFull : 0;

    /// <summary>Leaves the old room, joins, tells the others (82507) and publishes the room.</summary>
    private async Task JoinAndAnnounceAsync(GameSession s, Room room, long area)
    {
        long self = s.Role!.Id;
        if (_byMember.TryGetValue(self, out var old) && old != room) await LeaveRoomAsync(s, old, notifySelf: false);
        Join(s, room, area, Now);
        await SyncAsync(s);
        var enter = new BarRoomUserEnterNotify { UserId = self };
        foreach (var (role, _, _) in room.Members.ToArray())
            if (role != self && Online(role) is { } member) await member.SendGameAsync(UserEnter, enter);
        await PublishAsync(room, except: self);
    }

    private void Join(GameSession s, Room room, long area, long now)
    {
        long self = s.Role!.Id;
        room.Members.Add((self, area != 0 ? area : s.Role.Area, now));
        if (room.Owner == self && !room.Speakers.Contains(self)) room.Speakers.Insert(0, self);
        _byMember[self] = room;
        foreach (var key in _invites.Keys.Where(k => k.Target == self && k.Room == room.Id).ToArray()) _invites.Remove(key);
        s.Tavern!.BarRoomId = room.Id;
        s.TavernCountedAt = now;
    }

    /// <summary>82483.</summary>
    public async Task ExitAsync(GameSession s, ExitBarRoomReq req)
    {
        if (!_byMember.TryGetValue(s.Role!.Id, out var room)) { await s.SendGameAsync(Exit, new ExitBarRoomResp { Result = ErrNotInRoom }); return; }
        await LeaveRoomAsync(s, room, notifySelf: true);
    }

    /// <summary>
    /// The sender leaves its room: today's time is counted up to now, the owner's room passes to the earliest member
    /// (inferred) and an empty room closes. <paramref name="notifySelf"/> sends 82483 result 0, on which the client
    /// drops the room (LC_BarRoomHelper.OnExitBarRoom) and its chat channel.
    /// </summary>
    private async Task LeaveRoomAsync(GameSession s, Room room, bool notifySelf)
    {
        await CountCoreAsync(s, sync: false);
        Remove(room, s.Role!.Id);
        await DetachAsync(s, notifySelf);
        if (_rooms.ContainsKey(room.Id)) await PublishAsync(room);
    }

    /// <summary>Takes a role out of a room's lists; closes the room when it empties.</summary>
    private void Remove(Room room, long role)
    {
        room.Members.RemoveAll(m => m.Role == role);
        room.Speakers.Remove(role);
        room.Requests.Remove(role);
        if (_byMember.TryGetValue(role, out var current) && current == room) _byMember.Remove(role);
        if (room.Members.Count == 0)
        {
            _rooms.Remove(room.Id);
            foreach (var key in _invites.Keys.Where(k => k.Room == room.Id).ToArray()) _invites.Remove(key);
            _log.LogInformation("Tavern room {Room} closed", room.Id);
            return;
        }
        if (room.Owner == role)
        {
            room.Owner = room.Members[0].Role;
            room.Muted.Remove(room.Owner);
            room.Requests.Remove(room.Owner);
            room.Speakers.Remove(room.Owner);
            room.Speakers.Insert(0, room.Owner);
        }
    }

    /// <summary>The member side of leaving: 5043 barRoomId 0 and, when asked, 82483.</summary>
    private async Task DetachAsync(GameSession s, bool notify)
    {
        if (s.Tavern == null) return;
        long left = s.Tavern.BarRoomId;
        s.Tavern.BarRoomId = 0;
        if (left != 0) foreach (var handler in Left) handler(s, left);
        s.TavernCountedAt = 0;
        if (!notify) return;
        await s.SendGameAsync(Exit, new ExitBarRoomResp { Result = 0 });
        await SyncAsync(s);
    }

    /// <summary>82508: the sender's room. Not in a room answers 2578 (the client's handler reads info unguarded).</summary>
    public async Task GetAsync(GameSession s)
    {
        if (_byMember.TryGetValue(s.Role!.Id, out var room))
            await s.SendGameAsync(Get, new BarRoomGetResp { Result = 0, Info = Detail(room) });
        else
            await s.SendGameAsync(Get, new BarRoomGetResp { Result = ErrNotInRoom });
    }

    // ------------------------------------------------------------------ invitations

    /// <summary>82484: any member may invite online players; each gets 82485 (LC_BarRoomHelper.OnInviteEnterBarRoomNotify).</summary>
    public async Task InviteAsync(GameSession s, InviteEnterBarRoomReq req)
    {
        long self = s.Role!.Id;
        var resp = new InviteEnterBarRoomResp { BarRoomGuid = req.BarRoomGuid };
        if (!InRoom(self, req.BarRoomGuid) || RoomById(req.BarRoomGuid) is not { } room)
        {
            resp.Result = ErrNotInRoom;
            await s.SendGameAsync(Invite, resp);
            return;
        }
        var notify = new InviteEnterBarRoomNotify
        {
            BarRoomGuid = room.Id, InviterGuid = self, RoomName = room.Name, InviterName = s.Role.Name,
            InviterJob = req.InviterJob, InviterLevel = req.InviterLevel,
        };
        foreach (long target in req.TargetGuidList.Distinct().Take(50))
        {
            if (target == self || room.Has(target) || room.Blocked.Contains(target) || Online(target) is not { } invited) continue;
            _invites[(target, room.Id)] = self;
            resp.TargetGuidList.Add(target);
            await invited.SendGameAsync(InviteNotify, notify);
        }
        await s.SendGameAsync(Invite, resp);
    }

    /// <summary>
    /// 82486: the invited player's answer. An accept joins the room; the answer goes to the invited player (who then
    /// asks for 82508 and opens the tavern window) and to the inviter (tips 59224 / 59225).
    /// </summary>
    public async Task RespondInviteAsync(GameSession s, ResponseInviteEnterBarRoomReq req)
    {
        long self = s.Role!.Id;
        var resp = new ResponseInviteEnterBarRoomResp { BarRoomGuid = req.BarRoomGuid, InviterGuid = req.InviterGuid, IsAccept = req.IsAccept };
        if (!_invites.Remove((self, req.BarRoomGuid), out long inviter) || inviter != req.InviterGuid)
        {
            resp.Result = RoomById(req.BarRoomGuid) == null ? ErrNoRoom : ErrData;
            await s.SendGameAsync(InviteResponse, resp);
            return;
        }
        if (req.IsAccept)
        {
            if (RoomById(req.BarRoomGuid) is not { } room) resp.Result = ErrNoRoom;
            else if (!room.Has(self))
            {
                resp.Result = CanJoin(room, self);
                if (resp.Result == 0) await JoinAndAnnounceAsync(s, room, s.Role.Area);
            }
            if (resp.Result == 0) resp.Info = Detail(RoomById(req.BarRoomGuid)!);
        }
        await s.SendGameAsync(InviteResponse, resp);
        if (resp.Result == 0 && Online(inviter) is { } host) await host.SendGameAsync(InviteResponse, resp);
    }

    // ------------------------------------------------------------------ owner settings

    private int CheckInfo(int type, int language, string name)
    {
        if (_data.Row("BarroomTypeConfig", type) is not { } typeRow || GameData.Int(typeRow, "_iType") != 0) return ErrType;
        // Language 0 is "unset" (inferred); a set language must be a BarroomTypeConfig _iType 1 row.
        if (language != 0 && (_data.Row("BarroomTypeConfig", language) is not { } langRow || GameData.Int(langRow, "_iType") != 1)) return ErrLanguage;
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl)) return ErrName;
        if (new System.Globalization.StringInfo(trimmed).LengthInTextElements > NameLength) return ErrNameLong;
        return 0;
    }

    /// <summary>The sender's room when the sender owns room <paramref name="guid"/>, else the error to answer.</summary>
    private (Room? Room, int Code) Owned(GameSession s, long guid)
    {
        if (RoomById(guid) is not { } room) return (null, ErrNoRoom);
        if (!room.Has(s.Role!.Id)) return (null, ErrNotInRoom);
        return room.Owner == s.Role.Id ? (room, 0) : (null, ErrNoPermission);
    }

    /// <summary>82494: type, language and name, at most once per BarRoomSetInfoCd seconds.</summary>
    public async Task SetInfoAsync(GameSession s, SetBarRoomInfoReq req)
    {
        var (room, code) = Owned(s, req.BarRoomGuid);
        long now = Now;
        if (room != null && now - room.InfoSetAt < InfoCooldown) code = ErrTooFast;
        if (code == 0) code = CheckInfo(req.BarRoomType, req.LanguageType, req.BarRoomName);
        if (code == 0)
        {
            room!.Type = req.BarRoomType;
            room.Language = req.LanguageType;
            room.Name = req.BarRoomName.Trim();
            room.InfoSetAt = now;
        }
        await s.SendGameAsync(SetInfo, new SetBarRoomInfoResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82489: hands the room to another member, at most once per BarRoomTransferRoomCd seconds.</summary>
    public async Task TransferAsync(GameSession s, TransferOwnerReq req)
    {
        var (room, code) = Owned(s, req.BarRoomGuid);
        long now = Now;
        if (code == 0)
            code = req.TargetGuid == s.Role!.Id || !room!.Has(req.TargetGuid) ? ErrNotInRoom
                : room.Blocked.Contains(req.TargetGuid) ? ErrTransferBlocked
                : room.Muted.Contains(req.TargetGuid) ? ErrTargetMuted
                : now < room.TransferCd ? ErrTransferCd : 0;
        if (code == 0)
        {
            room!.Owner = req.TargetGuid;
            room.TransferCd = now + TransferCooldown;
            room.Requests.Remove(req.TargetGuid);
            room.Speakers.Remove(req.TargetGuid);
            room.Speakers.Insert(0, req.TargetGuid);
        }
        await s.SendGameAsync(Transfer, new TransferOwnerResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82513: a GM sets a room's Popularity and the "special room" count.</summary>
    public async Task GmSetInfoAsync(GameSession s, SetGmBarRoomInfoReq req)
    {
        int code = !GmCommands.Allowed(_options.Value, s.Account, loopbackTool: false) ? ErrNoPermission
            : RoomById(req.BarRoomGuid) == null ? ErrNoRoom : 0;
        var room = RoomById(req.BarRoomGuid);
        if (code == 0)
        {
            if (req.Hotvalue > 0) room!.Hot = req.Hotvalue;
            room!.Hot = (int)Math.Clamp((long)room.Hot + req.HotvalueAdd, 0, int.MaxValue);
            room.GmSpecial = req.GmSpRoomCount;
        }
        await s.SendGameAsync(GmSetInfo, new SetGmBarRoomInfoResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    // ------------------------------------------------------------------ the mic

    /// <summary>82487: asks the owner for the mic (Set and Order modes; Free needs no request).</summary>
    public async Task ApplyMicAsync(GameSession s, ApplyOpenMicReq req)
    {
        long self = s.Role!.Id;
        var room = RoomById(req.BarRoomGuid);
        int code = room == null ? ErrNoRoom : !room.Has(self) ? ErrNotInRoom
            : room.Mode == SpeakAuthorityType.Free ? ErrFreeMic
            : room.Speakers.Contains(self) ? ErrOnMic
            : room.Requests.ContainsKey(self) ? ErrApplied
            : room.Muted.Contains(self) ? ErrTargetMuted : 0;
        if (code == 0) room!.Requests[self] = (req.Reason ?? "").Length > 60 ? req.Reason![..60] : req.Reason ?? "";
        await s.SendGameAsync(ApplyMic, new ApplyOpenMicResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82492: the owner answers a request; an approval puts the member on the mic list (BarroomSpeakNum at most).</summary>
    public async Task HandleMicAsync(GameSession s, OwnerHandleOpenMicReq req)
    {
        var (room, code) = Owned(s, req.BarRoomGuid);
        if (code == 0 && !room!.Requests.ContainsKey(req.ApplyerId)) code = ErrNotApplied;
        if (code == 0 && req.IsAgree && room!.Speakers.Count >= SpeakerLimit) code = ErrMicFull;
        if (code == 0)
        {
            room!.Requests.Remove(req.ApplyerId);
            if (req.IsAgree && room.Has(req.ApplyerId) && !room.Speakers.Contains(req.ApplyerId)) room.Speakers.Add(req.ApplyerId);
        }
        await s.SendGameAsync(HandleMic, new OwnerHandleOpenMicResp { Result = code, IsAgree = req.IsAgree });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82488: the sender leaves the mic list (the owner cannot).</summary>
    public async Task DownMicAsync(GameSession s, DownMicReq req)
    {
        long self = s.Role!.Id;
        var room = RoomById(req.BarRoomGuid);
        int code = room == null ? ErrNoRoom : !room.Has(self) ? ErrNotInRoom : room.Owner == self ? ErrOwnerMic
            : !room.Speakers.Contains(self) ? ErrNotOnMic : 0;
        if (code == 0) room!.Speakers.Remove(self);
        await s.SendGameAsync(DownMic, new DownMicResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82490: the mic mode (Free, Set, Order), optionally with the new mic list.</summary>
    public async Task MicModeAsync(GameSession s, ModifyControlMicTypeReq req)
    {
        var (room, code) = Owned(s, req.BarRoomGuid);
        if (code == 0 && !Enum.IsDefined(req.ControlMicType)) code = ErrData;
        if (code == 0)
        {
            room!.Mode = req.ControlMicType;
            if (room.Mode == SpeakAuthorityType.Free) room.Requests.Clear();
            if (req.MicOrder != null) ApplyOrder(room, req.MicOrder);
        }
        var resp = new ModifyControlMicTypeResp { Result = code };
        if (code == 0) resp.MicOrder = MicList(room!);
        await s.SendGameAsync(MicMode, resp);
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82493: the owner orders the mic list (micOrder 1..n); members not listed leave it.</summary>
    public async Task MicOrderAsync(GameSession s, SetMicOrderReq req)
    {
        var (room, code) = Owned(s, req.BarRoomGuid);
        if (code == 0 && req.SpeakUsers.Count > SpeakerLimit) code = ErrMicFull;
        if (code == 0) ApplyOrder(room!, req);
        await s.SendGameAsync(MicOrder, new SetMicOrderResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    private void ApplyOrder(Room room, SetMicOrderReq order)
    {
        var listed = order.SpeakUsers.OrderBy(u => u.MicOrder).Select(u => u.UserId)
            .Where(id => id != room.Owner && room.Has(id) && !room.Muted.Contains(id)).Distinct()
            .Take(Math.Max(0, SpeakerLimit - 1)).ToList();
        room.Speakers.Clear();
        room.Speakers.Add(room.Owner);
        room.Speakers.AddRange(listed);
        foreach (long id in listed) room.Requests.Remove(id);
    }

    private static SetMicOrderReq MicList(Room room)
    {
        var list = new SetMicOrderReq { BarRoomGuid = room.Id };
        for (int i = 0; i < room.Speakers.Count; i++) list.SpeakUsers.Add(new CurSpeakUsers { UserId = room.Speakers[i], MicOrder = i + 1 });
        return list;
    }

    // ------------------------------------------------------------------ moderation

    /// <summary>The target of an owner action: a member other than the owner.</summary>
    private (Room? Room, int Code) Target(GameSession s, long guid, long target, bool member = true)
    {
        var (room, code) = Owned(s, guid);
        if (code != 0) return (null, code);
        if (target == s.Role!.Id || member && !room!.Has(target)) return (null, ErrData);
        return (room, 0);
    }

    /// <summary>82498: mutes a member (off the mic list and its requests) until unmuted.</summary>
    public async Task ForbidAsync(GameSession s, ForbiddenUserReq req)
    {
        var (room, code) = Target(s, req.BarRoomGuid, req.TargetGuid);
        if (code == 0)
        {
            room!.Muted.Add(req.TargetGuid);
            room.Speakers.Remove(req.TargetGuid);
            room.Requests.Remove(req.TargetGuid);
        }
        await s.SendGameAsync(Forbid, new ForbiddenUserResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82499.</summary>
    public async Task CancelForbidAsync(GameSession s, CanecelForbiddenUserReq req)
    {
        var (room, code) = Target(s, req.BarRoomGuid, req.TargetGuid, member: false);
        if (code == 0 && !room!.Muted.Remove(req.TargetGuid)) code = ErrNotMuted;
        await s.SendGameAsync(CancelForbid, new CanecelForbiddenUserResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82500: removes a member and blocks them from coming back; the kicked player gets 82483 result 0.</summary>
    public async Task KickAsync(GameSession s, KickUserReq req)
    {
        var (room, code) = Target(s, req.BarRoomGuid, req.TargetGuid, member: false);
        if (code == 0 && !room!.Has(req.TargetGuid) && room.Blocked.Contains(req.TargetGuid)) code = ErrData;
        if (code == 0)
        {
            room!.Blocked.Add(req.TargetGuid);
            if (room.Has(req.TargetGuid))
            {
                Remove(room, req.TargetGuid);
                if (Online(req.TargetGuid) is { } target)
                    _loop.PostOwned(target, async () =>
                    {
                        await CountCoreAsync(target, sync: false);
                        if (!_byMember.ContainsKey(req.TargetGuid)) await DetachAsync(target, notify: true);
                    }, "tavern kick");
            }
        }
        await s.SendGameAsync(Kick, new KickUserResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82501: takes a player off the block list.</summary>
    public async Task CancelKickAsync(GameSession s, CancelKickUserReq req)
    {
        var (room, code) = Target(s, req.BarRoomGuid, req.TargetGuid, member: false);
        if (code == 0 && !room!.Blocked.Remove(req.TargetGuid)) code = ErrNotBlocked;
        await s.SendGameAsync(CancelKick, new CancelKickUserResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>82504 / 82505: "mute all" closes every mic but the owner's (isCloseMic).</summary>
    public async Task ShutUpAllAsync(GameSession s, long guid, bool closed)
    {
        var (room, code) = Owned(s, guid);
        if (code == 0) room!.MicClosed = closed;
        if (closed) await s.SendGameAsync(ShutUpAll, new ShutUpAllResp { Result = code });
        else await s.SendGameAsync(CancelShutUpAll, new CancelShutUpAllResp { Result = code });
        if (code == 0) await PublishAsync(room!);
    }

    /// <summary>
    /// 82514: the owner turns a member's mic off. The client sends it only to mute (LC_BarRoomHelper.SetOtherMicMute)
    /// and its handler (LC_DAL_BarRoom.MutePlayerInChannel) mutes the receiving client's own mic, so the answer goes to
    /// the target. A failure answers the sender.
    /// </summary>
    public async Task MutePlayerAsync(GameSession s, MuteBarRoomPlayerReq req)
    {
        var (room, code) = Target(s, req.BarRoomUid, req.TargetPlayerUid);
        var target = code == 0 ? Online(req.TargetPlayerUid) : null;
        if (code == 0 && target == null) code = ErrData;
        var resp = new MuteBarRoomPlayerResp
        {
            Result = code, GroupId = req.BarRoomUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IsMute = req.IsMute, TargetPlayerUid = req.TargetPlayerUid,
        };
        await (code == 0 ? target! : s).SendGameAsync(MutePlayer, resp);
    }

    // ------------------------------------------------------------------ gifts

    private (int Popularity, int Star)? GiftValues(int item)
    {
        if (_data.Row("ItemConfig", item) is not { } row || GameData.Int(row, "_iType") != GiftType || GameData.Int(row, "_iSubType") != GiftSubType)
            return null;
        int popularity = 0, star = 0;
        if (row.TryGetProperty("_iUseEffect", out var effects) && effects.ValueKind == JsonValueKind.Array)
            foreach (var effect in effects.EnumerateArray())
            {
                if (effect.ValueKind != JsonValueKind.Array || effect.GetArrayLength() < 2) continue;
                int key = int.TryParse(effect[0].ToString(), out var k) ? k : 0;
                int value = int.TryParse(effect[1].ToString(), out var v) ? v : 0;
                if (key == EffectPopularity) popularity = value;
                else if (key == EffectStar) star = value;
            }
        return (popularity, star);
    }

    /// <summary>
    /// 82491: pays the gift items, adds their Popularity (_iUseEffect 110) to the room and their Star Power (111) to the
    /// recipient's popularity and weekPopularity. The answer goes to the gifter, whose client posts the gift line in
    /// the room chat (LC_BarRoomHelper.OnPresentGift).
    /// </summary>
    public async Task GiftAsync(GameSession s, PresentGiftReq req)
    {
        long self = s.Role!.Id;
        var room = RoomById(req.BarRoomGuid);
        var values = GiftValues(req.GiftId);
        var target = Online(req.TargetGuid);
        int code = room == null ? ErrNoRoom : !room.Has(self) ? ErrNotInRoom
            : values == null || req.GiftNum is <= 0 or > 9999 ? ErrData
            : req.TargetGuid == self ? ErrGiftSelf
            : !room.Has(req.TargetGuid) ? ErrGiftLeft
            : target == null ? ErrGiftOffline : 0;
        if (code == 0 && await _character.ExchangeAsync(s, [(req.GiftId, (long)req.GiftNum)], [], popup: false) == null)
            code = ErrorCodes.ItemsShort;
        if (code != 0) { await s.SendGameAsync(Gift, new PresentGiftResp { Result = code }); return; }
        int hot = (int)Math.Min(int.MaxValue, (long)values!.Value.Popularity * req.GiftNum);
        int star = (int)Math.Min(int.MaxValue, (long)values.Value.Star * req.GiftNum);
        room!.Hot = (int)Math.Min(int.MaxValue, (long)room.Hot + hot);
        if (star > 0) _loop.PostOwned(target!, () => CreditStarAsync(target!, star), "tavern gift");
        await s.SendGameAsync(Gift, new PresentGiftResp
        {
            Result = 0, ItemId = req.GiftId, ItemNum = req.GiftNum, GifterUser = s.Role.Name, ReceiveUser = req.TargetGuid,
            UserBarRoomHot = star, HotAdd = hot,
        });
        await PublishAsync(room);
    }

    private async Task CreditStarAsync(GameSession s, int star)
    {
        if (s.Tavern is not { } info) return;
        RollDay(info, Now);
        info.Popularity = (int)Math.Min(int.MaxValue, (long)info.Popularity + star);
        info.WeekPopularity = (int)Math.Min(int.MaxValue, (long)info.WeekPopularity + star);
        await SaveAsync(s);
        foreach (var handler in Changed) handler(s);
        await SyncAsync(s);
    }
}
