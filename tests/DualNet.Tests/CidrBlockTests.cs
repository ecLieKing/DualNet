using System.Net;
using DualNet.Core.Networking;

namespace DualNet.Tests;

[TestClass]
public sealed class CidrBlockTests
{
    [TestMethod]
    public void Parse_DefaultsToSlash24_AndCalculatesRange()
    {
        Assert.IsTrue(CidrBlock.TryParse("192.168.50.10", out var block, out _));
        Assert.AreEqual(24, block!.PrefixLength);
        Assert.AreEqual("192.168.50.0", block.NetworkAddress.ToString());
        Assert.AreEqual("192.168.50.255", block.BroadcastAddress.ToString());
        Assert.AreEqual(254L, block.UsableHostCount);
    }

    [DataTestMethod]
    [DataRow("10.0.0.1/15")]
    [DataRow("10.0.0.1/31")]
    [DataRow("not-an-ip/24")]
    [DataRow("192.168.10.0/24")]
    [DataRow("192.168.10.255/24")]
    public void Parse_RejectsUnsafeOrInvalidInput(string value) => Assert.IsFalse(CidrBlock.TryParse(value, out _, out _));

    [TestMethod]
    public void RecommendLocalAddress_UsesDot200_AndAvoidsTargets()
    {
        CidrBlock.TryParse("192.168.3.200/24", out var block, out _);
        var result = block!.RecommendLocalAddress([IPAddress.Parse("192.168.3.201")]);
        Assert.AreEqual("192.168.3.202", result.ToString());
    }

    [TestMethod]
    public void Slash16_IsMaximumScanRange()
    {
        Assert.IsTrue(CidrBlock.TryParse("172.16.20.1/16", out var block, out _));
        Assert.AreEqual(65534L, block!.UsableHostCount);
    }

    [TestMethod]
    public void NetworkParserAcceptsCanonicalNetworkAddress()
    {
        Assert.IsTrue(CidrBlock.TryParseNetwork("192.168.10.0/24", out var block, out _));
        Assert.AreEqual("192.168.10.0/24", block!.ToNetworkString());
    }
}
