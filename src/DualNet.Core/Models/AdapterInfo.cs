using System.Net;

namespace DualNet.Core.Models;

public sealed record AdapterInfo
{
    public required Guid Id { get; init; }
    public required int InterfaceIndex { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Status { get; init; }
    public required string PhysicalMediaType { get; init; }
    public required bool IsPhysical { get; init; }
    public bool Ipv4BindingEnabled { get; init; } = true;
    public bool Ipv6BindingEnabled { get; init; } = true;
    public required bool DhcpEnabled { get; init; }
    public bool DnsAutomatic { get; init; } = true;
    public required bool AutomaticMetric { get; init; }
    public required int InterfaceMetric { get; init; }
    public required IReadOnlyList<IpAddressInfo> Addresses { get; init; }
    public required IReadOnlyList<string> DnsServers { get; init; }
    public required IReadOnlyList<RouteInfo> Routes { get; init; }

    public bool IsUp => Status.Equals("Up", StringComparison.OrdinalIgnoreCase);
    public bool HasDefaultRoute => Routes.Any(r => r.DestinationPrefix == "0.0.0.0/0");
    public RouteInfo? DefaultRoute => Routes.Where(r => r.DestinationPrefix == "0.0.0.0/0")
        .OrderBy(r => r.RouteMetric).FirstOrDefault();
    public string DisplayName => $"{Name} · {Description}{(IsUp ? "" : "（未连接，可配置）")}";
    public IPAddress? PrimaryAddress => Addresses.Select(a => IPAddress.TryParse(a.Address, out var ip) ? ip : null)
        .FirstOrDefault(ip => ip is not null && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
    public IPAddress? ProbeSourceAddress => PrimaryAddress ?? Addresses.Select(a => IPAddress.TryParse(a.Address, out var ip) ? ip : null).FirstOrDefault(ip => ip is not null);
}

public sealed record IpAddressInfo(string Address, int PrefixLength, string PrefixOrigin, string SuffixOrigin, bool SkipAsSource);

public sealed record RouteInfo(string DestinationPrefix, string NextHop, int RouteMetric, string Protocol, bool Publish);
