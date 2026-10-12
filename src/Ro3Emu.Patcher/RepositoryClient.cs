using System.Net.Http.Headers;
using System.Text.Json;

namespace Ro3Emu.Patcher;

/// <summary>Reads a release repository over HTTP(S). Only <c>channels/*.json</c> and <c>news.json</c> are fetched uncached.</summary>
public sealed class RepositoryClient : IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsClient;

    public RepositoryClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/");
        }
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                MaxConnectionsPerServer = 16,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ro3EmuLauncher/1.0");
    }

    public Uri BaseUri => _http.BaseAddress!;

    public async Task<ChannelInfo> GetChannelAsync(string channel, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"channels/{channel}.json");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new PatcherException($"Channel '{channel}' does not exist on the update server.");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ChannelInfo>(stream, Json.Compact, ct)
            ?? throw new PatcherException("The update server returned an empty channel.");
    }

    public async Task<(Manifest Manifest, byte[] Raw)> GetManifestAsync(ChannelInfo channel, CancellationToken ct)
    {
        PathRules.ValidateRelative(channel.Manifest);
        byte[] raw = await _http.GetByteArrayAsync(channel.Manifest, ct);
        if (!string.Equals(Hashing.Sha256Hex(raw), channel.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PatcherException("The release manifest is corrupt (hash mismatch). Try again in a moment.");
        }
        return (Manifest.FromCompressedBytes(raw), raw);
    }

    /// <summary>Downloads one stored chunk; <paramref name="onBytes"/> reports compressed bytes as they arrive.</summary>
    public async Task<byte[]> GetChunkAsync(string hash, int storedSize, Action<int>? onBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(PathRules.ChunkRelative(hash), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[storedSize];
        int total = 0;
        while (total < storedSize)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(64 * 1024, storedSize - total)), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
            onBytes?.Invoke(read);
        }
        if (total != storedSize || await stream.ReadAsync(new byte[1], ct) != 0)
        {
            throw new IOException($"Chunk {hash[..12]} has the wrong size.");
        }
        return buffer;
    }

    public async Task<byte[]?> TryGetBytesAsync(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>An expected failure whose message can be shown to the player as is.</summary>
public sealed class PatcherException(string message, Exception? inner = null) : Exception(message, inner);
