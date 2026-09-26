using DualNet.Core.Models;
using DualNet.Core.Networking;

namespace DualNet.Tests;

[TestClass]
public sealed class RoutingRuleTests
{
    [TestMethod]
    public void HostNormalizesToSlash32AndSuggestsDot200()
    {
        Assert.IsTrue(RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _));
        Assert.AreEqual("192.168.10.6/32", rule!.DestinationPrefix);
        Assert.AreEqual("192.168.10.0/24", rule.SourceNetworkPrefix);
        Assert.AreEqual("192.168.10.200", rule.LocalAddress);
    }

    [DataTestMethod]
    [DataRow("192.168.10", "192.168.10.0/24", "192.168.10.x")]
    [DataRow("10.20.0.99/16", "10.20.0.0/16", "10.20.0.0/16")]
    public void SubnetAcceptsShorthandAndCanonicalizes(string input, string destination, string display)
    {
        Assert.IsTrue(RoutingRuleParser.TryParse(RoutingRuleKind.Subnet, input, out var rule, out _));
        Assert.AreEqual(destination, rule!.DestinationPrefix);
        Assert.AreEqual(display, rule.DisplayTarget);
    }

    [TestMethod]
    public void SubnetCanCoverHostWithoutChangingHostIdentity()
    {
        RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var host, out _);
        RoutingRuleParser.TryParse(RoutingRuleKind.Subnet, "192.168.10", out var subnet, out _);
        Assert.IsTrue(RoutingRuleParser.IsCoveredBySubnet(host!, [host!, subnet!]));
        Assert.AreEqual("192.168.10.6/32", host!.DestinationPrefix);
    }

    [TestMethod]
    public void DetectionPolicyNeverTreatsPingOnlyAsDevice()
    {
        var falsePositive = new DeviceVerificationResult("192.168.10.99", false, string.Empty, true);
        var actualDevice = new DeviceVerificationResult("192.168.10.6", true, "02-11-22-33-44-55", false);
        Assert.IsFalse(falsePositive.IsDetected);
        Assert.IsTrue(actualDevice.IsDetected);
    }
}
