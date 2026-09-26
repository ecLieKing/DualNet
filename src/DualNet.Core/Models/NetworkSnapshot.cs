namespace DualNet.Core.Models;

public sealed record NetworkSnapshot
{
    public int Version { get; init; } = 1;
    public required string MachineName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public Guid? TargetAdapterId { get; init; }
    // v2 compatibility fields.
    public Guid? IndustrialAdapterId { get; init; }
    public Guid? UplinkAdapterId { get; init; }
    public required IReadOnlyList<AdapterSnapshot> Adapters { get; init; }
}

public sealed record AdapterSnapshot
{
    public required Guid Id { get; init; }
    public required int InterfaceIndex { get; init; }
    public required string Name { get; init; }
    public required bool DhcpEnabled { get; init; }
    public required bool AutomaticMetric { get; init; }
    public required int InterfaceMetric { get; init; }
    public bool Ipv4BindingEnabled { get; init; } = true;
    public bool Ipv6BindingEnabled { get; init; } = true;
    public required IReadOnlyList<IpAddressInfo> Addresses { get; init; }
    public required IReadOnlyList<string> DnsServers { get; init; }
    public required IReadOnlyList<RouteInfo> Routes { get; init; }

    public static AdapterSnapshot From(AdapterInfo adapter) => new()
    {
        Id = adapter.Id,
        InterfaceIndex = adapter.InterfaceIndex,
        Name = adapter.Name,
        DhcpEnabled = adapter.DhcpEnabled,
        AutomaticMetric = adapter.AutomaticMetric,
        InterfaceMetric = adapter.InterfaceMetric,
        Ipv4BindingEnabled = adapter.Ipv4BindingEnabled,
        Ipv6BindingEnabled = adapter.Ipv6BindingEnabled,
        Addresses = adapter.Addresses.ToArray(),
        DnsServers = (adapter.DnsServers ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
        Routes = adapter.Routes.Where(r => !r.Protocol.Equals("Local", StringComparison.OrdinalIgnoreCase)).ToArray()
    };
}
