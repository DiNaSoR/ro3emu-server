using System.Buffers.Binary;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Voice;

/// <summary>
/// The 77015/77016 handoff from the encrypted game gate to the plaintext voice gate. Ours (the client's message
/// table has no such id; 77010-77014 are already taken by ServerImAdapter/Diagnostics/FeedbackButton/PassChallenges).
/// Routed like FeedbackService.SubmitMsg: AdmitRole, since it only reads and writes the sender's own ticket.
/// </summary>
public sealed class VoiceTicketService(IOptions<ServerOptions> options, VoiceTicketRegistry tickets) : Game.Messaging.IMessageModule
{
    /// <summary>Up: empty body.</summary>
    public const uint TicketReq = 77015;

    /// <summary>Down: u16 port | i64 roleId | 16-byte token | u32 ttlSeconds, little-endian. No host field: the
    /// client dials whatever host it already used for the game gate.</summary>
    public const uint Ticket = 77016;

    public void Map(Game.Messaging.MessageRoutes routes)
    {
        routes.AdmitRole(id => id == TicketReq);
        routes.Map(TicketReq, "voice", async (session, _, _) =>
        {
            var voice = options.Value.Voice;
            long roleId = session.Role?.Id ?? 0;
            if (!options.Value.VoiceServed() || session.Role == null || session.Account == null)
            {
                await session.SendGameRawAsync(Ticket, Encode(0, roleId, new byte[16], 0));
                return;
            }
            byte[] token = tickets.Issue(session.Account, roleId, voice.TicketSeconds);
            await session.SendGameRawAsync(Ticket, Encode((ushort)voice.Port, roleId, token, voice.TicketSeconds));
        });
    }

    private static byte[] Encode(ushort port, long roleId, byte[] token, int ttlSeconds)
    {
        var body = new byte[2 + 8 + 16 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0, 2), port);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(2, 8), roleId);
        token.AsSpan(0, 16).CopyTo(body.AsSpan(10, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(26, 4), (uint)ttlSeconds);
        return body;
    }
}
