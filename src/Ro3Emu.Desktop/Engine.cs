using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.Desktop;

/// <summary>What the window does: server status, the client DLL update and the join flow (patch, log in, start).</summary>
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
    /// Puts the newest CI build of the client DLLs into client\ next to this exe (Play copies them into the game on every
    /// patch). The current ones are kept in client\backups\&lt;time&gt;. Returns the build's commit.
    /// </summary>
    public static async Task<string> UpdateClientDllsAsync(Action<string> log, CancellationToken ct, string? releaseBase = null, string? clientDir = null)
    {
        releaseBase ??= DllRelease;
        clientDir ??= Path.Combine(AppDir, "client");
        using var http = Http();
        string commit = (await http.GetStringAsync(releaseBase + "commit.txt", ct)).Trim();
        return await DownloadAsync(http, commit, log, ct, releaseBase, clientDir);
    }

    /// <summary>The file in client\ that records which commit its DLLs were downloaded from.</summary>
    public const string CommitFile = "commit.txt";

    /// <summary>
    /// Run before every Play: asks the CI release which commit it holds and downloads the DLLs only when that differs
    /// from client\commit.txt or a DLL is missing. Returns the commit and whether anything was downloaded.
    /// </summary>
    public static async Task<(string Commit, bool Updated)> UpdateIfNewerAsync(Action<string> log, CancellationToken ct, string? releaseBase = null, string? clientDir = null)
    {
        releaseBase ??= DllRelease;
        clientDir ??= Path.Combine(AppDir, "client");
        using var http = Http();
        string latest;
        using (var check = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            check.CancelAfter(TimeSpan.FromSeconds(15)); // a slow GitHub must not hold up Play for long
            latest = (await http.GetStringAsync(releaseBase + "commit.txt", check.Token)).Trim();
        }
        var recorded = Path.Combine(clientDir, CommitFile);
        string? have = File.Exists(recorded) ? File.ReadAllText(recorded).Trim() : null;
        if (have == latest && ClientDlls.All(d => File.Exists(Path.Combine(clientDir, d)))) return (Short(latest), false);
        return (await DownloadAsync(http, latest, log, ct, releaseBase, clientDir), true);
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RO3EmuDesktop/1.0");
        return http;
    }

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;

    private static async Task<string> DownloadAsync(HttpClient http, string commit, Action<string> log, CancellationToken ct, string releaseBase, string clientDir)
    {
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
        await File.WriteAllTextAsync(Path.Combine(clientDir, CommitFile), commit + Environment.NewLine, ct);
        return Short(commit);
    }

    /// <summary>The join command line (RO3Emu.exe's own options); the password is added in memory only, by PlayAsync.</summary>
    public static List<string> JoinArguments(string address, string? account, string? clientDir)
    {
        var args = new List<string> { "join", address.Trim(), "--yes" };
        if (!string.IsNullOrWhiteSpace(account)) args.AddRange(["--account", account.Trim()]);
        if (!string.IsNullOrWhiteSpace(clientDir)) args.AddRange(["--client", clientDir.Trim()]);
        return args;
    }

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>
    /// RO3Emu.exe join, run in this process (Ro3Emu.Setup.Flows.JoinAsync): checks the game folder, fetches the server's
    /// gate key, patches the game with client\*.dll, makes the English text map, logs in and starts the game. Its
    /// console output goes to log line by line. Returns null on success, or what went wrong and what to do.
    /// </summary>
    public static async Task<string?> PlayAsync(string address, string? account, string? password, string? clientDir, Action<string> log)
    {
        var args = JoinArguments(address, account, clientDir);
        if (!string.IsNullOrEmpty(account) && !string.IsNullOrEmpty(password)) args.AddRange(["--password", password]);
        await OneAtATime.WaitAsync();
        var console = Console.Out;
        using var writer = new LineWriter(log);
        try
        {
            Setup.Ui.Interactive = false; // never wait for an answer on a console nobody sees
            Console.SetOut(writer);
            var options = Setup.Options.Parse([.. args]);
            Setup.SetupLog.Write($"RO3EmuDesktop join {address} {(account != null ? "as " + account : "")}");
            await Task.Run(() => Setup.Flows.JoinAsync(options));
            return null;
        }
        catch (Setup.SetupException e) { return e.Message + Environment.NewLine + e.Fix; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
        {
            return e.Message;
        }
        finally
        {
            writer.Flush();
            Console.SetOut(console);
            OneAtATime.Release();
        }
    }

    /// <summary>Console output as lines; a progress line redrawn with \r keeps only its last state.</summary>
    private sealed class LineWriter(Action<string> log) : TextWriter
    {
        private readonly System.Text.StringBuilder _line = new();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\r') { _line.Clear(); return; }
                if (value != '\n') { _line.Append(value); return; }
                Emit();
            }
        }

        public override void Flush() { lock (_line) Emit(); }

        private void Emit()
        {
            var text = _line.ToString().TrimEnd();
            _line.Clear();
            if (text.Trim().Length > 0) log(text);
        }
    }
}
