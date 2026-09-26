using System.Net;
using System.Net.Sockets;
using DualNet.Core.Models;

namespace DualNet.Core.Networking;

public static class RoutingRuleParser
{
    public static bool TryParse(RoutingRuleKind kind, string? input, out RoutingRule? rule, out string error)
    {
        rule = null;
        error = string.Empty;
        if (kind == RoutingRuleKind.Host)
        {
            if (string.IsNullOrWhiteSpace(input) || input.Contains('/') || !IPAddress.TryParse(input.Trim(), out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                error = "单个设备请输入完整 IPv4，例如 192.168.10.6。";
                return false;
            }
            var octets = ip.GetAddressBytes();
            var sourceBlock = CidrBlock.FromAddress(new IPAddress([octets[0], octets[1], octets[2], 0]), 24);
            var local = sourceBlock.RecommendLocalAddress([ip]);
            rule = new RoutingRule
            {
                Kind = RoutingRuleKind.Host,
                DestinationPrefix = $"{ip}/32",
                SourceNetworkPrefix = sourceBlock.ToNetworkString(),
                LocalAddress = local.ToString()
            };
            return true;
        }

        if (!CidrBlock.TryParseNetwork(input, out var block, out error)) return false;
        var localAddress = block!.RecommendLocalAddress();
        rule = new RoutingRule
        {
            Kind = RoutingRuleKind.Subnet,
            DestinationPrefix = block.ToNetworkString(),
            SourceNetworkPrefix = block.ToNetworkString(),
            LocalAddress = localAddress.ToString()
        };
        return true;
    }

    public static bool ValidateLocalAddress(RoutingRule rule, out string error)
    {
        error = string.Empty;
        if (!CidrBlock.TryParseNetwork(rule.SourceNetworkPrefix, out var source, out _)
            || !IPAddress.TryParse(rule.LocalAddress, out var local)
            || !source!.IsUsableHost(local))
        {
            error = $"本机地址 {rule.LocalAddress} 不属于规则 {rule.DisplayTarget} 的目标地址域。";
            return false;
        }
        if (rule.Kind == RoutingRuleKind.Host && rule.DestinationPrefix.StartsWith(rule.LocalAddress + "/", StringComparison.Ordinal))
        {
            error = "本机地址不能与目标设备地址相同。";
            return false;
        }
        return true;
    }

    public static bool IsCoveredBySubnet(RoutingRule host, IEnumerable<RoutingRule> rules)
    {
        if (host.Kind != RoutingRuleKind.Host) return false;
        var address = IPAddress.Parse(host.DestinationPrefix[..host.DestinationPrefix.IndexOf('/')]);
        return rules.Where(r => r.Kind == RoutingRuleKind.Subnet)
            .Any(r => CidrBlock.TryParseNetwork(r.DestinationPrefix, out var subnet, out _) && subnet!.Contains(address));
    }
}
