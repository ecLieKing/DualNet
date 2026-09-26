using DualNet.Core.Models;
using DualNet.Core.Networking;

namespace DualNet.Tests;

[TestClass]
public sealed class AdapterRecommendationTests
{
    [TestMethod]
    public void RecommendTargetPrefersConnectedDefaultRouteThenPhysicalAndMetric()
    {
        var disconnected = Create("断开的以太网", false, false, true, 5);
        var virtualUp = Create("VPN", true, false, false, 10);
        var physicalUp = Create("USB 网卡", true, false, true, 30);
        var defaultRoute = Create("Wi-Fi", true, true, true, 40);

        var result = AdapterRecommendationService.RecommendTarget([disconnected, virtualUp, physicalUp, defaultRoute]);

        Assert.AreEqual(defaultRoute.Id, result!.Id);
    }

    [TestMethod]
    public void RecommendTargetCanReturnVirtualOrDisconnectedAdapter()
    {
        var disconnected = Create("断开的蜂窝网卡", false, false, true, 5);
        var virtualAdapter = Create("VPN", true, false, false, 25);

        Assert.AreEqual(virtualAdapter.Id, AdapterRecommendationService.RecommendTarget([disconnected, virtualAdapter])!.Id);
        Assert.AreEqual(disconnected.Id, AdapterRecommendationService.RecommendTarget([disconnected])!.Id);
    }

    [TestMethod]
    public void RecommendInternetAdapterPrefersAnotherAdapterWithDefaultRoute()
    {
        var target = Create("WLAN", true, true, true, 10);
        var usb = Create("USB 网络共享", true, true, true, 25);
        var vpn = Create("VPN", true, false, false, 5);

        var result = AdapterRecommendationService.RecommendInternetAdapter([target, vpn, usb], target.Id);

        Assert.AreEqual(usb.Id, result!.Id);
    }

    private static AdapterInfo Create(string name, bool up, bool defaultRoute, bool physical, int metric) => new()
    {
        Id = Guid.NewGuid(), InterfaceIndex = metric, Name = name, Description = name,
        Status = up ? "Up" : "Disconnected", PhysicalMediaType = string.Empty, IsPhysical = physical,
        DhcpEnabled = true, AutomaticMetric = true, InterfaceMetric = metric,
        Addresses = [], DnsServers = [],
        Routes = defaultRoute ? [new RouteInfo("0.0.0.0/0", "192.168.10.1", 0, "Dhcp", false)] : []
    };
}
