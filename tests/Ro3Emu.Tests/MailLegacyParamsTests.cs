using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class MailLegacyParamsTests
{
    private static byte[] Params(params LanguageParam[] values)
    {
        var data = new LanguageData();
        data.Param.AddRange(values);
        return data.ToByteArray();
    }
    private static LanguageParam Int(long value) => new() { Tp = LanguageParamType.Int, IntParam = value };
    private static LanguageParam Str(string value) => new() { Tp = LanguageParamType.Str, StrParam = value };

    [Fact]
    public void AwardLettersWrittenWithStringParametersAreSentWithoutThem()
    {
        // In-game 11.10: pre-fix award letters logged "id:0 is not in AuctionTabConfig" on every mail sync.
        var legacy = new MailService.Letter { TemplateId = 1042, ContentParams = Params(Int(500), Str("Zeny"), Str("Guild"), Str("Sword")) };
        var current = new MailService.Letter { TemplateId = 1045, ContentParams = Params(Int(500), Int(1), Int(1003), Int(2090450)) };
        var other = new MailService.Letter { TemplateId = 1040, ContentParams = Params(Str("Sword")) };
        Assert.True(MailService.LegacyAwardParams(legacy));
        Assert.False(MailService.LegacyAwardParams(current));
        Assert.False(MailService.LegacyAwardParams(other));
    }
}
