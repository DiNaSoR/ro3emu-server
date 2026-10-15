namespace Ro3Emu.Server.Game.Messaging;

/// <summary>
/// One domain's game messages: its routes, the handlers it intercepts and how its messages are admitted. Modules are
/// mapped once at startup in registration order (ServerHost), which is also the order of their interceptors.
/// </summary>
public interface IMessageModule
{
    void Map(MessageRoutes routes);
}
