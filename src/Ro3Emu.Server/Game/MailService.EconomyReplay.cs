using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class MailService
{
    // A natural delivery source identifies the requested reward, not its assigned mail ID or delivery time.
    private static string DeliverySemanticHash(Letter letter, DateTimeOffset? expiresAt, int days)
    {
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            letter.Category, letter.Title, letter.Content, letter.Abstract, letter.Sender,
            letter.SenderId, letter.TemplateId, letter.ImportantType, letter.DeliveryKey,
            letter.ContentParams, letter.Attachments,
            Items = letter.Items.Select(ItemValue.Capture).ToArray(),
            Currency = letter.Currency.OrderBy(x => x.Key).Select(x => new { x.Key, x.Value }).ToArray(),
            ExplicitExpiry = expiresAt?.ToUnixTimeSeconds(),
            NeverExpires = expiresAt == DateTimeOffset.MaxValue,
            RelativeDays = expiresAt == null ? days : 0,
        });
        return Convert.ToHexString(SHA256.HashData(request));
    }
}
