using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>
/// Tavern rooms on the voice relay (VoiceRooms.Belongs and MicHeldOff): a room member joins the room's channel, its
/// TavernService room id sent as a number like a party; TavernService.MayTalk holds the mic; leaving the room drops
/// the member on the next sweep.
/// </summary>
public sealed class TavernVoiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private SyntheticHost _host = null!;
    private int _voicePort;

    public async Task InitializeAsync()
    {
        _voicePort = TestPorts.FreePort();
        _host = await SyntheticHost.StartAsync(o => o.Voice.Port = _voicePort);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task<(long Role, VoiceTestClient Voice)> VoiceOf(BotClient bot)
    {
        var (role, key) = await VoiceRelayTests.GetTicketAsync(bot, _voicePort);
        var voice = new VoiceTestClient(_voicePort) { Tag = role.ToString() };
        await voice.ConnectAsync();
        await voice.AuthAsync(role, key);
        await voice.WaitTypeAsync(VoiceDown.AuthOk, Wait);
        return (role, voice);
    }

    private static async Task<byte> Join(VoiceTestClient voice, long room)
    {
        await voice.JoinAsync(room.ToString());
        return (await voice.WaitTypeAsync(VoiceDown.JoinResult, Wait)).Payload[0];
    }

    private static bool MicStateOf(byte type, byte[] payload, long role, bool on) =>
        type == VoiceDown.MicState && BitConverter.ToInt64(payload) == role && payload[8] == (on ? 1 : 0);

    private static async Task<long> Open(BotClient owner)
    {
        await owner.SendGameAsync(TavernService.Create, new CreateBarRoomReq { BarRoomType = 1, BarRoomName = "Pub", AreaId = 1 });
        var created = await Next(owner, TavernService.Create, CreateBarRoomResp.Parser);
        Assert.Equal(0, created.Result);
        return created.Info.BarRoomSummary.BarRoomGuid;
    }

    private static async Task Enter(BotClient bot, long room)
    {
        await bot.SendGameAsync(TavernService.Enter, new EnterBarRoomReq { BarRoomGuid = room, AreaId = 1 });
        Assert.Equal(0, (await Next(bot, TavernService.Enter, EnterBarRoomResp.Parser)).Result);
    }

    private static async Task SetMicMode(BotClient owner, long room, SpeakAuthorityType mode)
    {
        await owner.SendGameAsync(TavernService.MicMode, new ModifyControlMicTypeReq { BarRoomGuid = room, ControlMicType = mode });
        Assert.Equal(0, (await Next(owner, TavernService.MicMode, ModifyControlMicTypeResp.Parser)).Result);
    }

    [Fact]
    public async Task RoomMembersJoinTheRoomsChannelAndOthersAreRefused()
    {
        await using var alice = await _host.InWorldAsync("Alice");
        await using var bob = await _host.InWorldAsync("Bob");
        await using var carol = await _host.InWorldAsync("Carol");
        long room = await Open(alice);
        await Enter(bob, room);
        var (_, aliceVoice) = await VoiceOf(alice);
        var (bobRole, bobVoice) = await VoiceOf(bob);
        var (_, carolVoice) = await VoiceOf(carol);
        await using var av = aliceVoice; await using var bv = bobVoice; await using var cv = carolVoice;

        Assert.Equal(VoiceJoinCode.Ok, await Join(aliceVoice, room));
        Assert.Equal(VoiceJoinCode.Ok, await Join(bobVoice, room));
        Assert.Equal(VoiceJoinCode.NotMember, await Join(carolVoice, room)); // not in the Tavern room

        // Free mode: Bob's mic is open and Alice hears him.
        await bobVoice.MicAsync(true);
        await aliceVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, true), Wait);
        await bobVoice.SendAudioAsync(0, [7]);
        Assert.Equal(bobRole, BitConverter.ToInt64((await aliceVoice.WaitTypeAsync(VoiceDown.Audio, Wait)).Payload));

        // Leaving the Tavern room drops Bob from the channel on the next membership sweep.
        await bob.SendGameAsync(TavernService.Exit, new ExitBarRoomReq { BarRoomGuid = room });
        Assert.Equal(0, (await Next(bob, TavernService.Exit, ExitBarRoomResp.Parser)).Result);
        await aliceVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == bobRole && p[8] == 0, TimeSpan.FromSeconds(3));
        Assert.Equal(VoiceKickCode.RemovedFromRoom, (await bobVoice.WaitTypeAsync(VoiceDown.Kicked, TimeSpan.FromSeconds(3))).Payload[0]);
    }

    [Fact]
    public async Task TheRoomsMicRulesHoldTheMicOffUntilTheOwnerGivesItBack()
    {
        await using var alice = await _host.InWorldAsync("Alice");
        await using var bob = await _host.InWorldAsync("Bob");
        long room = await Open(alice);
        await Enter(bob, room);
        var (_, aliceVoice) = await VoiceOf(alice);
        var (bobRole, bobVoice) = await VoiceOf(bob);
        await using var av = aliceVoice; await using var bv = bobVoice;
        Assert.Equal(VoiceJoinCode.Ok, await Join(aliceVoice, room));
        Assert.Equal(VoiceJoinCode.Ok, await Join(bobVoice, room));
        await bobVoice.MicAsync(true);
        await aliceVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, true), Wait);

        // Manual mode: only the owner and the mic list may talk, so the next sweep holds Bob's mic off.
        await SetMicMode(alice, room, SpeakAuthorityType.Manual);
        await aliceVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, false), TimeSpan.FromSeconds(3));
        await bobVoice.SendAudioAsync(1, [9]);
        await aliceVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Audio, TimeSpan.FromMilliseconds(500));

        // Back to Free mode: Bob still wants his mic, so it comes back and his voice flows again.
        await SetMicMode(alice, room, SpeakAuthorityType.Free);
        await aliceVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, true), TimeSpan.FromSeconds(3));
        await bobVoice.SendAudioAsync(2, [7]);
        Assert.Equal(bobRole, BitConverter.ToInt64((await aliceVoice.WaitTypeAsync(VoiceDown.Audio, Wait)).Payload));
    }
}
