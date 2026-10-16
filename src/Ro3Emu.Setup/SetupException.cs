namespace Ro3Emu.Setup;

/// <summary>A failure the player can fix: what went wrong and what to do, both in plain words (no stack trace).</summary>
public class SetupException(string message, string fix) : Exception(message)
{
    public string Fix { get; } = fix;
}

/// <summary>A failure while installing or updating the game from the publisher.</summary>
public sealed class InstallException(string message, string fix) : SetupException(message, fix);
