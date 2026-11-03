using DualNet.Core.Services;

namespace DualNet.Tests;

[TestClass]
public sealed class NetworkInventoryTests
{
    [TestMethod]
    public async Task UsbAndVirtualAdapterNullDnsEntriesAreFilteredAtCapture()
    {
        var id = Guid.NewGuid();
        var json = $$"""
        [{
          "Id":"{{id}}", "InterfaceIndex":50, "Name":"USB 网络共享",
          "Description":"Remote NDIS Compatible Device", "Status":"Up",
          "PhysicalMediaType":"NdisPhysicalMedium802_3", "IsPhysical":true,
          "Ipv4BindingEnabled":true, "Ipv6BindingEnabled":true,
          "DhcpEnabled":true, "AutomaticMetric":true, "InterfaceMetric":25,
          "Addresses":[], "DnsServers":[null,"","10.64.10.178"], "Routes":[]
        }]
        """;

        var adapters = await new NetworkInventoryService(new JsonRunner(json)).GetAdaptersAsync();

        CollectionAssert.AreEqual(new[] { "10.64.10.178" }, adapters.Single().DnsServers.ToArray());
    }

    [TestMethod]
    public async Task InventoryCapturesWhetherDnsIsAutomatic()
    {
        var id = Guid.NewGuid();
        var json = $$"""
        [{
          "Id":"{{id}}", "InterfaceIndex":15, "Name":"WLAN", "Description":"Wi-Fi",
          "Status":"Up", "PhysicalMediaType":"NdisPhysicalMediumNative802_11", "IsPhysical":true,
          "DhcpEnabled":true, "DnsAutomatic":false, "AutomaticMetric":true, "InterfaceMetric":25,
          "Addresses":[], "DnsServers":["1.1.1.1"], "Routes":[]
        }]
        """;
        var runner = new JsonRunner(json);

        var adapter = (await new NetworkInventoryService(runner).GetAdaptersAsync()).Single();

        Assert.IsFalse(adapter.DnsAutomatic);
        StringAssert.Contains(runner.Script!, "-Name NameServer");
        Assert.IsFalse(runner.Script!.Contains("-PolicyStore", StringComparison.Ordinal));
    }

    private sealed class JsonRunner(string json) : IPowerShellRunner
    {
        public string? Script { get; private set; }
        public Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default)
        {
            Script = script;
            return Task.FromResult(json);
        }
    }
}
