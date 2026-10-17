using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>The server list names the gates by the IP address the client reached it on (issue: joined by public IP, host started with its LAN IP).</summary>
public sealed class ServerListGateHostTests
{
    [Theory]
    [InlineData("203.0.113.7", "192.168.1.20", "203.0.113.7")]  // joined through the public IP of a host started on its LAN IP
    [InlineData("192.168.1.20", "203.0.113.7", "192.168.1.20")] // a LAN friend of a host started with --public
    [InlineData("198.51.100.4", "198.51.100.4", "198.51.100.4")]
    [InlineData("127.0.0.1", "127.0.0.1", "127.0.0.1")]         // local play
    [InlineData("127.0.0.1", "192.168.1.20", "192.168.1.20")]   // loopback never leaks to other players
    [InlineData("example.org", "198.51.100.4", "198.51.100.4")]  // a host name keeps the configured address
    [InlineData("", "198.51.100.4", "198.51.100.4")]
    [InlineData(null, "198.51.100.4", "198.51.100.4")]
    [InlineData("0.0.0.0", "198.51.100.4", "198.51.100.4")]
    [InlineData("[2001:db8::5]", "198.51.100.4", "[2001:db8::5]")]
    [InlineData("[::1]", "127.0.0.1", "127.0.0.1")]
    public void GatesFollowTheAddressTheClientUsed(string? requestHost, string publicHost, string expected) =>
        Assert.Equal(expected, SdkEndpoints.GateHost(requestHost, publicHost));
}
