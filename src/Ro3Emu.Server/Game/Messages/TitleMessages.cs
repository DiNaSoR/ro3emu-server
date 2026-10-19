namespace Ro3Emu.Server.Game;

/// <summary>43102 TITLE_LEVEL (<see cref="TitleService"/>): the role's own state only.</summary>
public sealed class TitleMessages(TitleService titles) : Messaging.IMessageModule
{
    public void Map(Messaging.MessageRoutes routes) =>
        routes.Map(TitleService.Upgrade, "titles", (s, _, _) => titles.UpgradeAsync(s));
}
