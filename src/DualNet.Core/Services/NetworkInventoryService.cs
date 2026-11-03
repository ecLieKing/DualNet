using System.Text.Json;
using DualNet.Core.Models;

namespace DualNet.Core.Services;

public sealed class NetworkInventoryService(IPowerShellRunner runner)
{
    private readonly IPowerShellRunner _runner = runner;

    public async Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
            $allAdapters = @(Get-NetAdapter -ErrorAction Stop)
            $allIpInterfaces = @(Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue)
            $allAddresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue)
            $allRoutes = @(Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue)
            $allDns = @(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue)
            $allV4Bindings = @(Get-NetAdapterBinding -ComponentID ms_tcpip -ErrorAction SilentlyContinue)
            $allV6Bindings = @(Get-NetAdapterBinding -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue)
            $items = @($allAdapters | ForEach-Object {
              $a = $_
              $idx = [int]$a.ifIndex
              $ipif = $allIpInterfaces | Where-Object { $_.InterfaceIndex -eq $idx } | Select-Object -First 1
              $ips = @($allAddresses | Where-Object { $_.InterfaceIndex -eq $idx } | ForEach-Object {
                [pscustomobject]@{ Address=$_.IPAddress; PrefixLength=[int]$_.PrefixLength; PrefixOrigin=[string]$_.PrefixOrigin; SuffixOrigin=[string]$_.SuffixOrigin; SkipAsSource=[bool]$_.SkipAsSource }
              })
              $routes = @($allRoutes | Where-Object { $_.InterfaceIndex -eq $idx } | ForEach-Object {
                [pscustomobject]@{ DestinationPrefix=$_.DestinationPrefix; NextHop=$_.NextHop; RouteMetric=[int]$_.RouteMetric; Protocol=[string]$_.Protocol; Publish=([string]$_.Publish -eq 'Yes') }
              })
              $dns = @(($allDns | Where-Object { $_.InterfaceIndex -eq $idx } | Select-Object -First 1).ServerAddresses)
              $dnsRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($a.InterfaceGuid)"
              $configuredDns = [string](Get-ItemProperty -LiteralPath $dnsRegistryPath -Name NameServer -ErrorAction SilentlyContinue).NameServer
              $v4binding = $allV4Bindings | Where-Object { $_.InterfaceDescription -eq $a.InterfaceDescription } | Select-Object -First 1
              $v6binding = $allV6Bindings | Where-Object { $_.InterfaceDescription -eq $a.InterfaceDescription } | Select-Object -First 1
              [pscustomobject]@{
                Id=[string]$a.InterfaceGuid; InterfaceIndex=$idx; Name=[string]$a.Name; Description=[string]$a.InterfaceDescription
                Status=[string]$a.Status; PhysicalMediaType=[string]$a.NdisPhysicalMedium; IsPhysical=[bool]$a.HardwareInterface
                Ipv4BindingEnabled=($null -eq $v4binding -or [bool]$v4binding.Enabled); Ipv6BindingEnabled=($null -eq $v6binding -or [bool]$v6binding.Enabled)
                DhcpEnabled=($ipif.Dhcp -eq 'Enabled'); DnsAutomatic=[string]::IsNullOrWhiteSpace($configuredDns); AutomaticMetric=($ipif.AutomaticMetric -eq 'Enabled'); InterfaceMetric=[int]$ipif.InterfaceMetric
                Addresses=$ips; DnsServers=$dns; Routes=$routes
              }
            })
            ConvertTo-Json -InputObject $items -Depth 8 -Compress
            """;

        var json = await _runner.ExecuteAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return [];
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var raw = JsonSerializer.Deserialize<List<RawAdapter>>(json, options) ?? [];
        return raw.Select(ToAdapter).OrderByDescending(a => a.IsUp).ThenBy(a => a.Name).ToArray();
    }

    private static AdapterInfo ToAdapter(RawAdapter raw) => new()
    {
        Id = Guid.Parse(raw.Id.Trim('{', '}')),
        InterfaceIndex = raw.InterfaceIndex,
        Name = raw.Name ?? string.Empty,
        Description = raw.Description ?? string.Empty,
        Status = raw.Status ?? string.Empty,
        PhysicalMediaType = raw.PhysicalMediaType ?? string.Empty,
        IsPhysical = raw.IsPhysical,
        Ipv4BindingEnabled = raw.Ipv4BindingEnabled,
        Ipv6BindingEnabled = raw.Ipv6BindingEnabled,
        DhcpEnabled = raw.DhcpEnabled,
        DnsAutomatic = raw.DnsAutomatic,
        AutomaticMetric = raw.AutomaticMetric,
        InterfaceMetric = raw.InterfaceMetric,
        Addresses = (raw.Addresses ?? []).Select(a => new IpAddressInfo(a.Address ?? string.Empty, a.PrefixLength, a.PrefixOrigin ?? string.Empty, a.SuffixOrigin ?? string.Empty, a.SkipAsSource)).ToArray(),
        DnsServers = (raw.DnsServers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray(),
        Routes = (raw.Routes ?? []).Select(r => new RouteInfo(r.DestinationPrefix ?? string.Empty, r.NextHop ?? string.Empty, r.RouteMetric, r.Protocol ?? string.Empty, r.Publish)).ToArray()
    };

    private sealed record RawAdapter
    {
        public string Id { get; init; } = string.Empty;
        public int InterfaceIndex { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public string? Status { get; init; }
        public string? PhysicalMediaType { get; init; }
        public bool IsPhysical { get; init; }
        public bool Ipv4BindingEnabled { get; init; } = true;
        public bool Ipv6BindingEnabled { get; init; } = true;
        public bool DhcpEnabled { get; init; }
        public bool DnsAutomatic { get; init; } = true;
        public bool AutomaticMetric { get; init; }
        public int InterfaceMetric { get; init; }
        public RawIpAddress[]? Addresses { get; init; }
        public string?[]? DnsServers { get; init; }
        public RawRoute[]? Routes { get; init; }
    }

    private sealed record RawIpAddress(string? Address, int PrefixLength, string? PrefixOrigin, string? SuffixOrigin, bool SkipAsSource);
    private sealed record RawRoute(string? DestinationPrefix, string? NextHop, int RouteMetric, string? Protocol, bool Publish);
}
