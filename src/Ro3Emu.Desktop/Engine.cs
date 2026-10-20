using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.Desktop;

/// <summary>What the window does: server status, the client DLL update and running RO3Emu.exe join.</summary>
public static class Engine
{
    public static string AppDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The rolling release CI publishes from main (.github/workflows/client-dlls.yml).</summary>
    public const string DllRelease = "https://github.com/akreao/ro3emu-server/releases/download/client-dlls/";
    public static readonly string[] ClientDlls = ["Ro3Emu.ClientHooks.dll", "Ro3Emu.Loc.dll"];

    /// <summary>Same rules as RO3Emu.exe (Flows.ServerUrl): host, host:port or http(s)://host[:port], default port 5380.</summary>
    public static string? ServerUrl(string address)
    {
        address = address.Trim().TrimEnd('/');
        if (address.Length == 0) return null;
        var withScheme = address.Contains("://") ? address : "http://" + address;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
            || uri.AbsolutePath != "/" || uri.Query.Length > 0) return null;
        bool portWritten = Regex.IsMatch(withScheme, @"^[A-Za-z]+://(\[[^\]]+\]|[^/:\[]+):\d+");
        return $"{uri.Scheme}://{uri.Host}:{(portWritten ? uri.Port : 5380)}";
    }

    /// <summary>Players online, or null when the server does not answer (GET /ro3emu/status).</summary>
    public static async Task<int?> StatusAsync(string address, CancellationToken ct)
    {
        var url = ServerUrl(address);
        if (url == null) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var json = JsonDocument.Parse(await http.GetStringAsync(url + "/ro3emu/status", ct));
            return json.RootElement.TryGetProperty("players", out var p) && p.TryGetInt32(out var n) ? n : 0;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Puts the newest CI build of the client DLLs into client\ next to RO3Emu.exe (RO3Emu.exe copies them into the game
    /// on every patch). The current ones are kept in client\backups\&lt;time&gt;. Returns the build's commit.
    /// </summary>
    public static async Task<string> UpdateClientDllsAsync(Action<string> log, CancellationToken ct, string? releaseBase = null, string? clientDir = null)
    {
        releaseBase ??= DllRelease;
        clientDir ??= Path.Combine(AppDir, "client");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RO3EmuDesktop/1.0");
        string commit = (await http.GetStringAsync(releaseBase + "commit.txt", ct)).Trim();
        var downloaded = new Dictionary<string, byte[]>();
        foreach (var dll in ClientDlls)
        {
            log($"downloading {dll}");
            downloaded[dll] = await http.GetByteArrayAsync(releaseBase + dll, ct);
            if (downloaded[dll].Length < 2 || downloaded[dll][0] != 'M' || downloaded[dll][1] != 'Z')
                throw new InvalidDataException($"{dll} from the release is not a DLL");
        }
        Directory.CreateDirectory(clientDir);
        var backup = Path.Combine(clientDir, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var (dll, bytes) in downloaded)
        {
            var target = Path.Combine(clientDir, dll);
            if (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) { log($"{dll} is already the newest"); continue; }
                Directory.CreateDirectory(backup);
                File.Copy(target, Path.Combine(backup, dll), overwrite: true);
            }
            var temp = target + ".new";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, target, overwrite: true);
            log($"updated {dll}");
        }
        return commit.Length > 12 ? commit[..12] : commit;
    }

    public static string? FindRo3Emu()
    {
        foreach (var name in new[] { "RO3Emu.exe", "RO3Emu" })
        {
            var path = Path.Combine(AppDir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The RO3Emu.exe command line for Play (the password goes in RO3EMU_PASSWORD, never on the command line).</summary>
    public static List<string> JoinArguments(string address, string? account, string? clientDir)
    {
        var args = new List<string> { "join", address.Trim(), "--yes" };
        if (!string.IsNullOrWhiteSpace(account)) args.AddRange(["--account", account.Trim()]);
        if (!string.IsNullOrWhiteSpace(clientDir)) args.AddRange(["--client", clientDir.Trim()]);
        return args;
    }

    /// <summary>Runs RO3Emu.exe join (patch for this server, log in, start the game); each output line goes to log.</summary>
    public static async Task<int> PlayAsync(string address, string? account, string? password, string? clientDir, Action<string> log, CancellationToken ct)
    {
        var exe = FindRo3Emu() ?? throw new FileNotFoundException("RO3Emu.exe is not next to RO3EmuDesktop.exe.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppDir,
        };
        foreach (var a in JoinArguments(address, account, clientDir)) start.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(account) && !string.IsNullOrEmpty(password)) start.Environment["RO3EMU_PASSWORD"] = password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("RO3Emu.exe did not start");
        process.StandardInput.Close(); // no questions: RO3Emu.exe takes the defaults
        var output = Pump(process.StandardOutput, log);
        var errors = Pump(process.StandardError, log);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(output, errors);
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, Action<string> log)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            // RO3Emu.exe redraws progress lines with \r; keep the last state of each.
            var text = line.Split('\r').Last().TrimEnd();
            if (text.Trim().Length > 0) log(text);
        }
    }
}
