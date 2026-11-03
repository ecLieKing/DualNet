namespace DualNet.Core.Models;

public enum RoutingMode
{
    PartialTraffic,
    AllTraffic
}

public enum RoutingRuleKind
{
    Host,
    Subnet
}

public sealed record RoutingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required RoutingRuleKind Kind { get; init; }
    public required string DestinationPrefix { get; init; }
    public required string SourceNetworkPrefix { get; init; }
    public required string LocalAddress { get; init; }
    public bool IsLocalAddressManual { get; init; }

    public string DisplayTarget
    {
        get
        {
            if (Kind == RoutingRuleKind.Host) return DestinationPrefix[..DestinationPrefix.IndexOf('/')];
            var slash = DestinationPrefix.IndexOf('/');
            var prefix = int.Parse(DestinationPrefix[(slash + 1)..]);
            if (prefix == 24)
            {
                var octets = DestinationPrefix[..slash].Split('.');
                return $"{octets[0]}.{octets[1]}.{octets[2]}.x";
            }
            return DestinationPrefix;
        }
    }
}

public sealed record RoutingProfile
{
    public int Version { get; init; } = 3;
    public RoutingMode Mode { get; init; } = RoutingMode.PartialTraffic;
    public Guid? TargetAdapterId { get; init; }
    public Guid? InternetAdapterId { get; init; }
    public AllTrafficSettings AllTraffic { get; init; } = new();
    // v1.1 compatibility fields. ProfileStore migrates IndustrialAdapterId to TargetAdapterId.
    public Guid? IndustrialAdapterId { get; init; }
    public Guid? UplinkAdapterId { get; init; }
    public IReadOnlyList<RoutingRule> Rules { get; init; } = [];
    public IReadOnlyList<RoutingRule> AppliedRules { get; init; } = [];
}

public sealed record AllTrafficSettings
{
    public string LocalAddress { get; init; } = string.Empty;
    public int PrefixLength { get; init; } = 24;
    public string Gateway { get; init; } = string.Empty;
    public IReadOnlyList<string> DnsServers { get; init; } = [];
}

public sealed record RouteConfiguration
{
    public required Guid TargetAdapterId { get; init; }
    public Guid? InternetAdapterId { get; init; }
    public required RoutingMode Mode { get; init; }
    public AllTrafficSettings AllTraffic { get; init; } = new();
    public IReadOnlyList<RoutingRule> Rules { get; init; } = [];
}

public sealed record ApplyRouteResult(RouteConfiguration Configuration, Guid? ConfirmationOperationId);

public sealed record DeviceVerificationResult(string IpAddress, bool ArpReachable, string MacAddress, bool PingReachable)
{
    public bool IsDetected => ArpReachable;
}
