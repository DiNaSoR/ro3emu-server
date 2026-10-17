using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ro3Emu.Tests;

/// <summary>
/// The suite runs as if the machine were in a fixed zone that no test realm uses (UTC+08, no DST), so a server path
/// that reads the host's zone (TimeZoneInfo.Local, DateTime.Now, a Kind=Local DateTime in new DateTimeOffset(..))
/// instead of the realm zone (the injected TimeProvider.LocalTimeZone) fails here as it would on a machine in Asia,
/// not only there. RO3EMU_TEST_HOST_TZ picks another zone id ("host" keeps the real one); RO3EMU_TEST_HOST_CULTURE
/// sets the current culture. .NET on Windows ignores TZ, hence the private-field override.
/// </summary>
internal static class HostZoneOverride
{
    public static readonly TimeZoneInfo Default =
        TimeZoneInfo.CreateCustomTimeZone("Ro3Emu test host +08", TimeSpan.FromHours(8), "Ro3Emu test host +08", "Ro3Emu test host +08");

    /// <summary>Elsewhere .NET honours TZ (an IANA id) once its zone cache is cleared; Asia/Singapore is +08 without DST.</summary>
    private const string UnixDefault = "Asia/Singapore";

    [ModuleInitializer]
    internal static void Apply()
    {
        string? id = Environment.GetEnvironmentVariable("RO3EMU_TEST_HOST_TZ");
        if (id != "host" && OperatingSystem.IsWindows())
            SetLocal(string.IsNullOrEmpty(id) ? Default : TimeZoneInfo.FindSystemTimeZoneById(id));
        else if (id != "host")
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrEmpty(id) ? UnixDefault : id);
            Environment.SetEnvironmentVariable("TZ", zone.Id);
            TimeZoneInfo.ClearCachedData();
            Verify(zone);
        }
        if (Environment.GetEnvironmentVariable("RO3EMU_TEST_HOST_CULTURE") is { Length: > 0 } name)
        {
            var culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        }
    }

    private static void SetLocal(TimeZoneInfo zone)
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        TimeZoneInfo.ClearCachedData();
        var cache = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        var local = cache?.GetType().GetField("_localTimeZone", Instance);
        // DateTime.Now reads the OS zone on Windows unless this one-year offset cache already holds the year.
        var oneYear = cache?.GetType().GetField("_oneYearLocalFromUtc", Instance);
        if (cache == null || local == null || oneYear == null)
            throw new InvalidOperationException("HostZoneOverride: TimeZoneInfo internals changed; set RO3EMU_TEST_HOST_TZ=host");
        local.SetValue(cache, zone);
        int year = DateTime.UtcNow.Year;
        var rule = zone.GetAdjustmentRules().FirstOrDefault(r => r.DateStart.Year <= year && year <= r.DateEnd.Year);
        oneYear.SetValue(cache, Activator.CreateInstance(oneYear.FieldType, Instance | BindingFlags.Public, null, [year, zone.BaseUtcOffset, rule], null));
        Verify(zone);
    }

    private static void Verify(TimeZoneInfo zone)
    {
        var utc = DateTime.UtcNow;
        if (TimeZoneInfo.Local.BaseUtcOffset != zone.BaseUtcOffset
            || Math.Abs((DateTime.Now - TimeZoneInfo.ConvertTimeFromUtc(utc, zone)).TotalMinutes) > 1
            || new DateTimeOffset(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Local)).Offset != zone.GetUtcOffset(new DateTime(2026, 10, 5)))
            throw new InvalidOperationException($"HostZoneOverride: {zone.Id} did not apply; set RO3EMU_TEST_HOST_TZ=host");
    }
}
