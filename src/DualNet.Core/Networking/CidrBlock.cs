using System.Net;
using System.Net.Sockets;

namespace DualNet.Core.Networking;

public sealed class CidrBlock
{
    private CidrBlock(IPAddress address, int prefixLength)
    {
        Address = address;
        PrefixLength = prefixLength;
        AddressValue = ToUInt32(address);
        Mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        NetworkValue = AddressValue & Mask;
        BroadcastValue = NetworkValue | ~Mask;
    }

    public IPAddress Address { get; }
    public int PrefixLength { get; }
    public uint AddressValue { get; }
    public uint NetworkValue { get; }
    public uint BroadcastValue { get; }
    public uint Mask { get; }
    public long UsableHostCount => PrefixLength >= 31 ? 0 : (long)BroadcastValue - NetworkValue - 1;
    public IPAddress NetworkAddress => FromUInt32(NetworkValue);
    public IPAddress BroadcastAddress => FromUInt32(BroadcastValue);

    public static bool TryParse(string? value, out CidrBlock? block, out string error)
    {
        block = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请输入设备 IPv4 地址和前缀，例如 192.168.10.10/24。";
            return false;
        }

        var parts = value.Trim().Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "设备地址必须是有效的 IPv4 地址。";
            return false;
        }

        var prefix = 24;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 16 or > 30))
        {
            error = "设备网段前缀仅支持 /16 到 /30。";
            return false;
        }

        block = new CidrBlock(ip, prefix);
        if (!block.IsUsableHost(ip))
        {
            error = "设备地址不能是网段地址或广播地址。";
            block = null;
            return false;
        }
        return true;
    }

    public static bool TryParseNetwork(string? value, out CidrBlock? block, out string error)
    {
        block = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请输入网段，例如 192.168.10 或 192.168.10.0/24。";
            return false;
        }

        var normalized = value.Trim();
        var shortParts = normalized.Split('.', StringSplitOptions.TrimEntries);
        if (!normalized.Contains('/') && shortParts.Length == 3)
        {
            if (shortParts.Any(p => !byte.TryParse(p, out _)))
            {
                error = "网段简写必须是三个 0–255 的数字，例如 192.168.10。";
                return false;
            }
            normalized += ".0/24";
        }
        else if (!normalized.Contains('/'))
        {
            normalized += "/24";
        }

        var parts = normalized.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "网段必须是有效的 IPv4 CIDR。";
            return false;
        }
        if (!int.TryParse(parts[1], out var prefix) || prefix is < 8 or > 30)
        {
            error = "目标网段前缀仅支持 /8 到 /30。";
            return false;
        }
        block = new CidrBlock(ip, prefix);
        return true;
    }

    public static CidrBlock FromAddress(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        return new CidrBlock(address, prefixLength);
    }

    public bool Contains(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork && (ToUInt32(ip) & Mask) == NetworkValue;

    public bool Overlaps(CidrBlock other) => NetworkValue <= other.BroadcastValue && other.NetworkValue <= BroadcastValue;

    public bool IsUsableHost(IPAddress ip)
    {
        if (!Contains(ip)) return false;
        var value = ToUInt32(ip);
        return value > NetworkValue && value < BroadcastValue;
    }

    public IPAddress RecommendLocalAddress(IEnumerable<IPAddress>? excluded = null)
    {
        var denied = new HashSet<uint>((excluded ?? []).Where(Contains).Select(ToUInt32)) { AddressValue };
        var preferred = NetworkValue + Math.Min(200u, Math.Max(1u, BroadcastValue - NetworkValue - 2));
        for (var candidate = preferred; candidate < BroadcastValue; candidate++)
            if (!denied.Contains(candidate)) return FromUInt32(candidate);
        for (var candidate = NetworkValue + 1; candidate < preferred; candidate++)
            if (!denied.Contains(candidate)) return FromUInt32(candidate);
        throw new InvalidOperationException("目标网段没有可用的本机地址。");
    }

    public IEnumerable<IPAddress> GetHosts()
    {
        for (var value = NetworkValue + 1; value < BroadcastValue; value++) yield return FromUInt32(value);
    }

    public string ToNetworkString() => $"{NetworkAddress}/{PrefixLength}";
    public override string ToString() => $"{Address}/{PrefixLength}";

    public static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) throw new ArgumentException("仅支持 IPv4。", nameof(ip));
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static IPAddress FromUInt32(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    });
}
