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

    private sealed class JsonRunner(string json) : IPowerShellRunner
    {
        public Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default) => Task.FromResult(json);
    }
}
