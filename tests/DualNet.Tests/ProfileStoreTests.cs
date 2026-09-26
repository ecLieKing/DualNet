using DualNet.Core.Models;
using DualNet.Core.Networking;
using DualNet.Core.Services;

namespace DualNet.Tests;

[TestClass]
public sealed class ProfileStoreTests
{
    [TestMethod]
    public async Task ProfileRoundTripsV2ModeTargetAndSettingsAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(root);
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);
            var target = Guid.NewGuid();
            var internet = Guid.NewGuid();
            await store.SaveAsync(new RoutingProfile
            {
                TargetAdapterId = target, InternetAdapterId = internet, Mode = RoutingMode.PartialTraffic,
                AllTraffic = new AllTrafficSettings { LocalAddress = "10.0.0.9", PrefixLength = 24, Gateway = "10.0.0.1", DnsServers = ["223.5.5.5"] },
                Rules = [rule!]
            });
            var loaded = await store.LoadAsync();
            Assert.AreEqual(target, loaded.TargetAdapterId);
            Assert.AreEqual(internet, loaded.InternetAdapterId);
            Assert.AreEqual(RoutingMode.PartialTraffic, loaded.Mode);
            Assert.AreEqual("10.0.0.1", loaded.AllTraffic.Gateway);
            Assert.AreEqual("192.168.10.6/32", loaded.Rules.Single().DestinationPrefix);
            Assert.IsFalse(File.Exists(store.ProfilePath + ".tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task V1ProfileMigratesIndustrialAdapterToPartialTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var target = Guid.NewGuid();
            var json = $$"""
            { "Version": 1, "IndustrialAdapterId": "{{target}}", "Rules": [] }
            """;
            await File.WriteAllTextAsync(Path.Combine(root, "profile.json"), json);

            var loaded = await new ProfileStore(root).LoadAsync();

            Assert.AreEqual(3, loaded.Version);
            Assert.AreEqual(RoutingMode.PartialTraffic, loaded.Mode);
            Assert.AreEqual(target, loaded.TargetAdapterId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task V2ProfileMigratesWithoutForcingInternetAdapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var target = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Combine(root, "profile.json"), $$"""
            { "Version": 2, "Mode": 0, "TargetAdapterId": "{{target}}", "Rules": [] }
            """);

            var loaded = await new ProfileStore(root).LoadAsync();

            Assert.AreEqual(3, loaded.Version);
            Assert.AreEqual(target, loaded.TargetAdapterId);
            Assert.IsNull(loaded.InternetAdapterId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
