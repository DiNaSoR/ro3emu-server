using System.Net;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

public sealed class ServerOptionsTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void LoopbackPublicHostUsesLoopbackListener(string publicHost)
    {
        var options = new ServerOptions { PublicHost = publicHost };

        Assert.Equal(IPAddress.Loopback, options.ListenAddress());
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("invalid-host")]
    public void NonLoopbackOrInvalidPublicHostUsesWildcardListener(string publicHost)
    {
        var options = new ServerOptions { PublicHost = publicHost };

        Assert.Equal(IPAddress.Any, options.ListenAddress());
    }

    [Theory]
    [InlineData("127.0.0.1", "192.0.2.5")]
    [InlineData("127.0.0.1", "0.0.0.0")]
    [InlineData("203.0.113.7", "127.0.0.1")]
    [InlineData("127.0.0.1", "::")]
    public void ExplicitBindAddressOverridesPublicHost(string publicHost, string bindAddress)
    {
        var options = new ServerOptions { PublicHost = publicHost, BindAddress = bindAddress };

        Assert.Equal(IPAddress.Parse(bindAddress), options.ListenAddress());
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("203.0.113.7", "0.0.0.0")]
    public void EmptyBindAddressFallsBackToPublicHostRule(string publicHost, string expected)
    {
        var options = new ServerOptions { PublicHost = publicHost, BindAddress = "" };

        Assert.Equal(IPAddress.Parse(expected), options.ListenAddress());
    }

    // Retain the activities branch's interface/NAT cases under master's canonical BindAddress contract.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.0.2.10")]
    public void ListenersUseTheConfiguredInterface(string host)
    {
        Assert.Equal(IPAddress.Parse(host), new ServerOptions { BindAddress = host }.ListenAddress());
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:0.0.0.0")]
    public void ExplicitWildcardBindAddressesArePreserved(string host)
    {
        Assert.Equal(IPAddress.Parse(host), new ServerOptions { BindAddress = host }.ListenAddress());
    }

    [Fact]
    public void NatAdvertisementDoesNotBindThePublicAddress()
    {
        var options = new ServerOptions { PublicHost = "203.0.113.10" };
        Assert.Equal(IPAddress.Any, options.ListenAddress());
        options.BindAddress = "192.0.2.10";
        Assert.Equal(IPAddress.Parse("192.0.2.10"), options.ListenAddress());
        Assert.Equal("203.0.113.10", options.PublicHost);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("192.0.2.999")]
    [InlineData("127.0.0.1:5380")]
    public void InvalidExplicitBindAddressIsRejected(string bindAddress)
    {
        Assert.Throws<FormatException>(() => new ServerOptions { BindAddress = bindAddress }.ListenAddress());
    }
}
