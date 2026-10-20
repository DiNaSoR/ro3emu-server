namespace Ro3Emu.Setup.Tests;

public class SetupTests
{
    [Theory]
    [InlineData("203.0.113.10", "http://203.0.113.10:5380")]
    [InlineData("203.0.113.10:6000", "http://203.0.113.10:6000")]
    [InlineData("play.example.com", "http://play.example.com:5380")]
    [InlineData("http://play.example.com/", "http://play.example.com:5380")]
    [InlineData("https://play.example.com:443", "https://play.example.com:443")]
    [InlineData("[2001:db8::1]:7000", "http://[2001:db8::1]:7000")]
    [InlineData("[2001:db8::1]", "http://[2001:db8::1]:5380")]
    [InlineData(" 10.0.0.2 ", "http://10.0.0.2:5380")]
    public void ServerAddresses(string address, string url) => Assert.Equal(url, Flows.ServerUrl(address));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("http://host/path")]
    public void NotServerAddresses(string address) => Assert.Null(Flows.ServerUrl(address));

    [Theory]
    [InlineData("ro3_Data/Managed/*.dll", "ro3_Data/Managed/HUD.dll", true)]
    [InlineData("ro3_Data/Managed/*.dll", "ro3_Data/Managed/sub/HUD.dll", false)]
    [InlineData("ro3_Data/**", "ro3_Data/Managed/sub/HUD.dll", true)]
    [InlineData("ro3.exe", "ro3.exe", true)]
    public void PathFilter(string pattern, string path, bool match) => Assert.Equal(match, InstallFlow.Glob(pattern)(path));

    [Fact]
    public void OptionsParse()
    {
        var o = Options.Parse(["host", "--public", "10.0.0.5", "--gm", "alice", "--gm", "bob", "--http-port", "6000", "--yes"]);
        Assert.Equal(("host", "10.0.0.5", 6000, true), (o.Command, o.PublicHost, o.Ports.Http, o.Yes));
        Assert.Equal(["alice", "bob"], o.GmAccounts);
        Assert.Equal("start", Options.Parse([]).Command);
        Assert.Equal(["1.2.3.4"], Options.Parse(["join", "1.2.3.4"]).Positional);
        Assert.Throws<SetupException>(() => Options.Parse(["--nope"]));
        Assert.Throws<SetupException>(() => Options.Parse(["fly"]));
    }

    [Fact]
    public async Task DeleteCharacterPostsTheFormAndReadsTheAnswer()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var http = new System.Net.HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        string? body = null, path = null;
        var serve = Task.Run(async () =>
        {
            var context = await http.GetContextAsync();
            path = context.Request.Url!.AbsolutePath;
            using (var reader = new StreamReader(context.Request.InputStream)) body = await reader.ReadToEndAsync();
            var answer = System.Text.Encoding.UTF8.GetBytes("""{"code":409,"message":"the character is in the game; log out first"}""");
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(answer);
            context.Response.Close();
        });

        var (code, message) = await Flows.PostDeleteRoleAsync(Flows.ServerUrl($"127.0.0.1:{port}")!, "alice", "p&ss", "Hero One");
        await serve;
        Assert.Equal((409, "the character is in the game; log out first"), (code, message));
        Assert.Equal("/ro3emu/account/delete-role", path);
        Assert.Equal("identity=alice&credential=p%26ss&role=Hero+One", body);
        Assert.Equal("delete-character", Options.Parse(["delete-character", "10.0.0.2"]).Command);
    }

    [Fact]
    public async Task SdkLoginPostsTheGamesFormAndReturnsTheToken()
    {
        var (url, answers, requests) = Serve("""{"code":0,"data":{"token":"tok-123"}}""", """{"code":401}""");
        Assert.Equal("tok-123", await Flows.SdkLoginAsync(url, "alice", "p&ss"));
        var wrong = await Assert.ThrowsAsync<SetupException>(() => Flows.SdkLoginAsync(url, "alice", "nope"));
        Assert.Contains("Wrong password", wrong.Message);
        await answers;
        Assert.Equal(["/sdk/login identity=alice&credential=p%26ss", "/sdk/login identity=alice&credential=nope"], requests);
        var o = Options.Parse(["join", "10.0.0.2", "--account", "alice", "--password", "pw"]);
        Assert.Equal(("alice", "pw"), (o.Account, o.Password));
    }

    /// <summary>A local HTTP server that answers each request with the next JSON body and records "path body".</summary>
    internal static (string Url, Task Done, List<string> Requests) Serve(params string[] bodies)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var http = new System.Net.HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var requests = new List<string>();
        var done = Task.Run(async () =>
        {
            try
            {
                foreach (var body in bodies)
                {
                    var context = await http.GetContextAsync();
                    using (var reader = new StreamReader(context.Request.InputStream))
                        requests.Add(context.Request.Url!.AbsolutePath + " " + await reader.ReadToEndAsync());
                    var answer = System.Text.Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(answer);
                    context.Response.Close();
                }
            }
            finally { http.Close(); }
        });
        return ($"http://127.0.0.1:{port}", done, requests);
    }

    [Fact]
    public async Task DeleteCharacterWithNoServerSaysSo()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var e = await Assert.ThrowsAsync<SetupException>(() => Flows.PostDeleteRoleAsync($"http://127.0.0.1:{port}", "a", "b", "c"));
        Assert.Contains("does not answer", e.Message);
    }

    [Fact]
    public void TranslationsAreEmbeddedInFileOrder()
    {
        var translations = Flows.Translations();
        Assert.True(translations.Count >= 5);
        Assert.All(translations, t => Assert.Contains('\t', t));
    }
}
