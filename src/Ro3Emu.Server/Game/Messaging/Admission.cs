using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game.Messaging;

public enum AdmissionMode { Economic, Runtime, Session }

/// <summary>
/// How the dispatcher admits one game message before its handler runs: an economic request, a runtime continuation
/// or an ordinary session intent with the reservation keys it needs.
/// </summary>
public sealed record Admission(AdmissionMode Mode, string Origin, ReservationKey[] Keys, string? RealmLabel = null)
{
    public static Admission Economic { get; } = new(AdmissionMode.Economic, "dispatcher/economic request", []);

    public static Admission Runtime(string origin) => new(AdmissionMode.Runtime, origin, []);

    public static Admission Session(ReservationKey[] keys, string? realmLabel = null) =>
        new(AdmissionMode.Session, "dispatcher", keys, realmLabel);
}
