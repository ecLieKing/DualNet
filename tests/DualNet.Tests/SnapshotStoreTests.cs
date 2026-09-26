using DualNet.Core.Models;
using DualNet.Core.Services;

namespace DualNet.Tests;

[TestClass]
public sealed class SnapshotStoreTests
{
    [TestMethod]
    public async Task BaselineIsAtomicAndNeverOverwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SnapshotStore(root);
            var first = CreateSnapshot(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            var second = CreateSnapshot(DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
            await store.SaveBaselineIfMissingAsync(first);
            await store.SaveBaselineIfMissingAsync(second);
            var loaded = await store.LoadBaselineAsync();
            Assert.AreEqual(first.CreatedAt, loaded!.CreatedAt);
            Assert.IsFalse(File.Exists(store.BaselinePath + ".tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WatchdogArmConfirmAndCleanupAreDeterministic()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SnapshotStore(root);
            var operation = Guid.NewGuid();
            await store.ArmWatchdogAsync(operation);
            Assert.IsTrue(store.IsWatchdogArmed(operation));
            await store.ConfirmWatchdogAsync(operation);
            Assert.IsTrue(store.IsWatchdogConfirmed(operation));
            Assert.IsFalse(store.IsWatchdogArmed(operation));
            store.ClearWatchdog(operation);
            Assert.IsFalse(store.IsWatchdogConfirmed(operation));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task WatchdogTimeoutRunsRecoveryButConfirmationDoesNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SnapshotStore(root);
            await store.SaveBaselineIfMissingAsync(CreateSnapshot(DateTimeOffset.Now));
            var timedOut = Guid.NewGuid();
            await store.ArmWatchdogAsync(timedOut);
            var recoveryCount = 0;
            var restored = await WatchdogCoordinator.WaitAndRecoverAsync(store, timedOut, TimeSpan.Zero,
                () => { recoveryCount++; return Task.CompletedTask; }, _ => Task.CompletedTask);
            Assert.IsTrue(restored);
            Assert.AreEqual(1, recoveryCount);

            var confirmed = Guid.NewGuid();
            await store.ArmWatchdogAsync(confirmed);
            await store.ConfirmWatchdogAsync(confirmed);
            restored = await WatchdogCoordinator.WaitAndRecoverAsync(store, confirmed, TimeSpan.Zero,
                () => { recoveryCount++; return Task.CompletedTask; }, _ => Task.CompletedTask);
            Assert.IsFalse(restored);
            Assert.AreEqual(1, recoveryCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task V2SnapshotWithoutBindingFieldsDefaultsToEnabledForSafeRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var adapterId = Guid.NewGuid();
            var json = $$"""
            {
              "Version": 2,
              "MachineName": "{{Environment.MachineName}}",
              "CreatedAt": "2026-08-07T00:00:00+08:00",
              "IndustrialAdapterId": "{{adapterId}}",
              "UplinkAdapterId": "{{Guid.NewGuid()}}",
              "Adapters": [{
                "Id": "{{adapterId}}", "InterfaceIndex": 2, "Name": "以太网",
                "DhcpEnabled": true, "AutomaticMetric": true, "InterfaceMetric": 25,
                "Addresses": [], "DnsServers": [], "Routes": []
              }]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(root, "baseline.json"), json);

            var loaded = await new SnapshotStore(root).LoadBaselineAsync();

            Assert.IsTrue(loaded!.Adapters.Single().Ipv4BindingEnabled);
            Assert.IsTrue(loaded.Adapters.Single().Ipv6BindingEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ExistingSnapshotFiltersNullAndBlankDnsEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SnapshotStore(root);
            var adapterId = Guid.NewGuid();
            await store.SaveBaselineIfMissingAsync(new NetworkSnapshot
            {
                Version = 3, MachineName = Environment.MachineName, CreatedAt = DateTimeOffset.Now,
                TargetAdapterId = adapterId,
                Adapters =
                [
                    new AdapterSnapshot
                    {
                        Id = adapterId, InterfaceIndex = 2, Name = "USB 网络共享", DhcpEnabled = false,
                        AutomaticMetric = true, InterfaceMetric = 25, Addresses = [], Routes = [],
                        DnsServers = [null!, "", "223.5.5.5"]
                    }
                ]
            });

            var loaded = await store.LoadBaselineAsync();

            CollectionAssert.AreEqual(new[] { "223.5.5.5" }, loaded!.Adapters.Single().DnsServers.ToArray());
            var script = NetworkConfigurationService.BuildRestoreScript(loaded);
            StringAssert.Contains(script, "'223.5.5.5'");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static NetworkSnapshot CreateSnapshot(DateTimeOffset created) => new()
    {
        MachineName = Environment.MachineName, CreatedAt = created,
        Version = 3, TargetAdapterId = Guid.NewGuid(), Adapters = []
    };
}
