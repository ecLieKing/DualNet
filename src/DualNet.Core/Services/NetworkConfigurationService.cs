using System.Net;
using System.Net.Sockets;
using System.Text;
using DualNet.Core.Models;
using DualNet.Core.Networking;

namespace DualNet.Core.Services;

public sealed class NetworkConfigurationService
{
    internal const int AddedAddressPrefixLength = 24;

    private readonly IPowerShellRunner _runner;
    private readonly NetworkInventoryService _inventory;
    private readonly SnapshotStore _snapshots;
    private readonly IEthernetProbeService _probe;

    public NetworkConfigurationService(IPowerShellRunner runner, NetworkInventoryService inventory, SnapshotStore snapshots, IEthernetProbeService? probe = null)
    {
        _runner = runner;
        _inventory = inventory;
        _snapshots = snapshots;
        _probe = probe ?? new EthernetProbeService();
    }

    public async Task<ApplyRouteResult> ApplyAsync(
        RouteConfiguration configuration,
        IReadOnlyList<AdapterInfo> adapters,
        Func<Guid, Task>? watchdogStarter = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selectedTarget = ValidateAndGetTarget(configuration, adapters);
        var baselineState = await GetOrCreateBaselineAsync(adapters, selectedTarget.Id, cancellationToken).ConfigureAwait(false);
        var baseline = baselineState.Snapshot;
        Guid? operationId = null;

        try
        {
            progress?.Report("正在保存网络恢复点…");
            await _snapshots.MarkPendingAsync(baseline, cancellationToken).ConfigureAwait(false);
            // Every update starts from the immutable original baseline. A first application already
            // is at that baseline and does not needlessly reset unrelated adapters.
            IReadOnlyList<AdapterInfo> currentAdapters = adapters;
            if (baselineState.WasExisting)
            {
                progress?.Report("正在读取当前路由状态…");
                currentAdapters = await _inventory.GetAdaptersAsync(cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyList<AdapterInfo> restoredAdapters = currentAdapters;
            NetworkSnapshot? restorePlan = null;
            if (baselineState.WasExisting)
            {
                progress?.Report("正在准备原始网卡配置…");
                var remappedBaseline = RemapSnapshot(baseline, currentAdapters);
                var changedAdapters = remappedBaseline.Adapters
                    .Where(snapshot => HasManagedDifference(currentAdapters.Single(adapter => adapter.Id == snapshot.Id), snapshot))
                    .ToArray();
                if (changedAdapters.Length > 0)
                {
                    restorePlan = remappedBaseline with { Adapters = changedAdapters };
                    restoredAdapters = ModelRestoredAdapters(currentAdapters, restorePlan);
                }
            }
            var target = restoredAdapters.SingleOrDefault(adapter => adapter.Id == configuration.TargetAdapterId)
                ?? throw new InvalidOperationException("恢复基线后找不到所选网卡。");
            var internetAdapter = ResolveInternetAdapter(configuration, restoredAdapters, target);
            progress?.Report(configuration.Mode == RoutingMode.PartialTraffic ? "正在检查本机地址…" : "正在检查默认路由参数…");
            var resolved = configuration.Mode == RoutingMode.PartialTraffic
                ? configuration with { Rules = await ResolvePartialAddressesAsync(target, configuration.Rules, cancellationToken).ConfigureAwait(false) }
                : configuration with { AllTraffic = ResolveAllTrafficSettings(target, configuration.AllTraffic) };

            string applyScript;
            if (resolved.Mode == RoutingMode.AllTraffic)
            {
                operationId = Guid.NewGuid();
                await _snapshots.ArmWatchdogAsync(operationId.Value, cancellationToken).ConfigureAwait(false);
                if (watchdogStarter is not null) await watchdogStarter(operationId.Value).ConfigureAwait(false);
                applyScript = BuildAllTrafficApplyScript(target, restoredAdapters, resolved.AllTraffic);
            }
            else
            {
                if (internetAdapter is not null) progress?.Report($"正在设置其余流量出口 {internetAdapter.Name}…");
                applyScript = BuildPartialApplyScript(target, resolved.Rules, internetAdapter, restoredAdapters);
            }
            if (restorePlan is not null) applyScript = BuildRestoreScript(restorePlan) + Environment.NewLine + applyScript;
            progress?.Report("正在写入地址和路由…");
            await _runner.ExecuteAsync(applyScript, cancellationToken).ConfigureAwait(false);

            progress?.Report("正在确认路由结果…");
            if (resolved.Mode == RoutingMode.AllTraffic)
                await VerifyAppliedAsync(resolved, restoredAdapters, cancellationToken).ConfigureAwait(false);
            if (operationId is null) _snapshots.ClearPending();
            progress?.Report("路由已生效。");
            await _snapshots.LogAsync($"v1.2 路由应用成功：模式={resolved.Mode}，目标网卡={target.Name}").ConfigureAwait(false);
            return new ApplyRouteResult(resolved, operationId);
        }
        catch (Exception exception)
        {
            await _snapshots.LogAsync($"应用失败，开始回滚：{exception.Message}").ConfigureAwait(false);
            if (operationId is not null) _snapshots.ClearWatchdog(operationId.Value);
            try { await RestoreSnapshotAsync(baseline, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception rollbackException) { await _snapshots.LogAsync($"自动回滚失败：{rollbackException.Message}").ConfigureAwait(false); }
            throw;
        }
    }

    public async Task RestoreBaselineAsync(CancellationToken cancellationToken = default)
    {
        using var restoreLock = new Semaphore(1, 1, "Global\\DualNet_Restore_Lock");
        if (!restoreLock.WaitOne(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("另一个恢复操作正在执行，请稍后重试。");
        try
        {
            var snapshot = await _snapshots.LoadBaselineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("没有可恢复的网络配置快照。");
            await RestoreSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _snapshots.ClearAll();
            await _snapshots.LogAsync("已恢复原始网络配置。").ConfigureAwait(false);
        }
        finally { restoreLock.Release(); }
    }

    public async Task ResetRoutingAsync(Guid targetAdapterId, Guid? internetAdapterId, RoutingMode mode,
        IReadOnlyList<RoutingRule> rules, CancellationToken cancellationToken = default)
    {
        using var restoreLock = new Semaphore(1, 1, "Global\\DualNet_Restore_Lock");
        if (!restoreLock.WaitOne(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("另一个网络重置操作正在执行，请稍后重试。");
        try
        {
            var baseline = await _snapshots.LoadBaselineAsync(cancellationToken).ConfigureAwait(false);
            var current = await _inventory.GetAdaptersAsync(cancellationToken).ConfigureAwait(false);
            var target = current.SingleOrDefault(adapter => adapter.Id == targetAdapterId)
                ?? throw new InvalidOperationException("找不到所选分流网卡，请刷新后重试。");
            AdapterInfo? internet = null;
            if (mode == RoutingMode.PartialTraffic)
            {
                if (internetAdapterId is Guid internetId)
                {
                    if (internetId != targetAdapterId)
                        internet = current.SingleOrDefault(adapter => adapter.Id == internetId)
                            ?? throw new InvalidOperationException("找不到所选其余流量出口，请刷新后重试。");
                }
            }

            var beforeReset = new NetworkSnapshot
            {
                Version = 3, MachineName = Environment.MachineName, CreatedAt = DateTimeOffset.Now,
                TargetAdapterId = targetAdapterId, Adapters = current.Select(AdapterSnapshot.From).ToArray()
            };
            try
            {
                string script;
                var strictBindingsChanged = baseline?.Adapters.Any(original =>
                {
                    var active = current.SingleOrDefault(adapter => adapter.Id == original.Id);
                    return active is not null
                        && (original.Ipv4BindingEnabled && !active.Ipv4BindingEnabled
                            || original.Ipv6BindingEnabled && !active.Ipv6BindingEnabled);
                }) == true;
                if ((mode == RoutingMode.AllTraffic || strictBindingsChanged) && baseline is not null)
                {
                    var remapped = RemapSnapshotToAvailableAdapters(baseline, current);
                    script = BuildRestoreScript(remapped)
                        + Environment.NewLine
                        + BuildEnableDhcpScript(target, FindSnapshot(remapped, target.Id));
                }
                else
                {
                    var currentById = current.ToDictionary(adapter => adapter.Id);
                    var remappedBaseline = baseline is null ? null : baseline with
                    {
                        Adapters = baseline.Adapters
                            .Where(snapshot => currentById.ContainsKey(snapshot.Id))
                            .Select(snapshot => snapshot with { InterfaceIndex = currentById[snapshot.Id].InterfaceIndex })
                            .ToArray()
                    };
                    script = BuildPartialResetScript(current, target, internet, rules, remappedBaseline);
                }
                await _runner.ExecuteAsync(script, cancellationToken).ConfigureAwait(false);
                await _snapshots.LogAsync("已清除路由配置并将所选网卡切换为 DHCP。").ConfigureAwait(false);
                _snapshots.ClearAll();
            }
            catch (Exception exception)
            {
                await _snapshots.LogAsync($"网络重置失败，开始恢复操作前状态：{exception.Message}").ConfigureAwait(false);
                try { await _runner.ExecuteAsync(BuildRestoreScript(beforeReset), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackException) { await _snapshots.LogAsync($"网络重置回滚失败：{rollbackException.Message}").ConfigureAwait(false); }
                throw;
            }
        }
        finally { restoreLock.Release(); }
    }

    public Task ConfirmStrictConfigurationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        _snapshots.ConfirmWatchdogAsync(operationId, cancellationToken);

    private static AdapterInfo ValidateAndGetTarget(RouteConfiguration configuration, IReadOnlyList<AdapterInfo> adapters)
    {
        var target = adapters.SingleOrDefault(adapter => adapter.Id == configuration.TargetAdapterId)
            ?? throw new InvalidOperationException("找不到所选分流网卡，请刷新后重试。");
        if (configuration.Mode == RoutingMode.PartialTraffic)
        {
            if (configuration.Rules.Count == 0) throw new InvalidOperationException("部分流量模式请至少添加一条目标规则。");
            if (configuration.Rules.Select(rule => rule.DestinationPrefix).Distinct(StringComparer.Ordinal).Count() != configuration.Rules.Count)
                throw new InvalidOperationException("分流规则中存在重复目标。");
            foreach (var rule in configuration.Rules)
                if (!RoutingRuleParser.ValidateLocalAddress(rule, out var error)) throw new InvalidOperationException(error);
            _ = ResolveInternetAdapter(configuration, adapters, target);
        }
        return target;
    }

    private static AdapterInfo? ResolveInternetAdapter(RouteConfiguration configuration, IReadOnlyList<AdapterInfo> adapters, AdapterInfo target)
    {
        if (configuration.Mode != RoutingMode.PartialTraffic) return null;
        if (configuration.InternetAdapterId is not Guid internetId)
            throw new InvalidOperationException("部分流量模式必须选择其余流量出口。");
        if (internetId == target.Id) throw new InvalidOperationException("分流网卡和其余流量出口不能选择同一张网卡。");
        var internetAdapter = adapters.SingleOrDefault(adapter => adapter.Id == internetId)
            ?? throw new InvalidOperationException("找不到所选其余流量出口，请刷新后重试。");
        if (!internetAdapter.HasDefaultRoute)
            throw new InvalidOperationException($"其余流量出口“{internetAdapter.Name}”没有默认路由。请先让该网卡获得网关。");
        return internetAdapter;
    }

    private async Task<IReadOnlyList<RoutingRule>> ResolvePartialAddressesAsync(AdapterInfo target, IReadOnlyList<RoutingRule> rules, CancellationToken cancellationToken)
    {
        var probeSource = target.ProbeSourceAddress ?? IPAddress.Any;
        var existingTexts = target.Addresses.Select(address => address.Address).ToHashSet(StringComparer.Ordinal);
        var chosenByNetwork = new Dictionary<string, string>(StringComparer.Ordinal);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<RoutingRule>(rules.Count);

        foreach (var rule in rules)
        {
            if (!CidrBlock.TryParseNetwork(rule.SourceNetworkPrefix, out var sourceBlock, out _))
                throw new InvalidOperationException($"规则 {rule.DisplayTarget} 的地址域无效。");
            if (chosenByNetwork.TryGetValue(rule.SourceNetworkPrefix, out var shared) && !rule.IsLocalAddressManual)
            {
                resolved.Add(rule with { LocalAddress = shared });
                continue;
            }

            var existingMatch = target.Addresses
                .Select(address => IPAddress.TryParse(address.Address, out var ip) ? ip : null)
                .FirstOrDefault(ip => ip is not null && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal) && sourceBlock!.IsUsableHost(ip));
            IPAddress local;
            if (!rule.IsLocalAddressManual && existingMatch is not null)
            {
                local = existingMatch;
            }
            else
            {
                local = IPAddress.Parse(rule.LocalAddress);
                if (rule.IsLocalAddressManual)
                {
                    var occupied = !existingTexts.Contains(local.ToString())
                                   && await _probe.ProbeMacAsync(local, probeSource, cancellationToken).ConfigureAwait(false) is not null;
                    if (occupied) throw new InvalidOperationException($"手动本机地址 {local} 已被其他设备使用。");
                }
                else
                {
                    local = await FindAvailableAddressAsync(sourceBlock!, local, probeSource, existingTexts, reserved, cancellationToken).ConfigureAwait(false);
                }
            }

            chosenByNetwork[rule.SourceNetworkPrefix] = local.ToString();
            reserved.Add(local.ToString());
            resolved.Add(rule with { LocalAddress = local.ToString() });
        }
        return resolved;
    }

    private async Task<IPAddress> FindAvailableAddressAsync(CidrBlock block, IPAddress preferred, IPAddress probeSource, HashSet<string> currentAddresses, HashSet<string> reserved, CancellationToken cancellationToken)
    {
        foreach (var candidate in EnumerateCandidates(block, preferred))
        {
            var text = candidate.ToString();
            if (reserved.Contains(text)) continue;
            if (currentAddresses.Contains(text)) return candidate;
            if (await _probe.ProbeMacAsync(candidate, probeSource, cancellationToken).ConfigureAwait(false) is null) return candidate;
        }
        throw new InvalidOperationException($"网段 {block.ToNetworkString()} 中没有可用的本机地址。");
    }

    private static IEnumerable<IPAddress> EnumerateCandidates(CidrBlock block, IPAddress preferred)
    {
        var preferredValue = CidrBlock.ToUInt32(preferred);
        for (var value = preferredValue; value < block.BroadcastValue; value++) yield return CidrBlock.FromUInt32(value);
        for (var value = block.NetworkValue + 1; value < preferredValue; value++) yield return CidrBlock.FromUInt32(value);
    }

    internal static AllTrafficSettings ResolveAllTrafficSettings(AdapterInfo target, AllTrafficSettings requested)
    {
        var existingAddress = target.Addresses.FirstOrDefault(address =>
            IPAddress.TryParse(address.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            && !address.Address.StartsWith("169.254.", StringComparison.Ordinal));
        var localText = string.IsNullOrWhiteSpace(requested.LocalAddress) ? existingAddress?.Address ?? string.Empty : requested.LocalAddress.Trim();
        if (!IPAddress.TryParse(localText, out var local) || local.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("全部流量模式需要有效的本机 IPv4 地址。");
        var prefix = string.IsNullOrWhiteSpace(requested.LocalAddress) && existingAddress is not null ? existingAddress.PrefixLength : requested.PrefixLength;
        if (prefix is < 1 or > 30) throw new InvalidOperationException("IPv4 前缀必须在 1 到 30 之间。");

        var existingDefault = target.DefaultRoute;
        var gatewayText = string.IsNullOrWhiteSpace(requested.Gateway) ? existingDefault?.NextHop ?? string.Empty : requested.Gateway.Trim();
        if (!IPAddress.TryParse(gatewayText, out var gateway) || gateway.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("全部流量模式需要有效的 IPv4 路由器地址。");
        if (!gateway.Equals(IPAddress.Any))
        {
            var block = CidrBlock.FromAddress(local, prefix);
            if (!block.IsUsableHost(gateway) || gateway.Equals(local))
                throw new InvalidOperationException("路由器地址必须与本机 IPv4 位于同一网段，且不能与本机地址相同。");
        }

        var dns = requested.DnsServers.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (dns.Any(value => !IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork))
            throw new InvalidOperationException("DNS 只能填写有效的 IPv4 地址。");
        return requested with { LocalAddress = local.ToString(), PrefixLength = prefix, Gateway = gateway.ToString(), DnsServers = dns };
    }

    private async Task<(NetworkSnapshot Snapshot, bool WasExisting)> GetOrCreateBaselineAsync(IReadOnlyList<AdapterInfo> adapters, Guid targetAdapterId, CancellationToken cancellationToken)
    {
        var existing = await _snapshots.LoadBaselineAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("现有快照属于另一台电脑，请勿继续写入。");
            if (existing.Version < 3)
                throw new InvalidOperationException("检测到旧版配置，请先点击“清除分流并启用 DHCP”，再应用新配置。");
            return (existing, true);
        }

        var snapshot = new NetworkSnapshot
        {
            Version = 3,
            MachineName = Environment.MachineName,
            CreatedAt = DateTimeOffset.Now,
            TargetAdapterId = targetAdapterId,
            Adapters = adapters.Select(AdapterSnapshot.From).ToArray()
        };
        await _snapshots.SaveBaselineIfMissingAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return (snapshot, false);
    }

    private async Task VerifyAppliedAsync(RouteConfiguration configuration, IReadOnlyList<AdapterInfo> originalAdapters, CancellationToken cancellationToken)
    {
        var refreshed = await _inventory.GetAdaptersAsync(cancellationToken).ConfigureAwait(false);
        var target = refreshed.SingleOrDefault(adapter => adapter.Id == configuration.TargetAdapterId)
            ?? throw new InvalidOperationException("应用后找不到目标网卡。");
        if (configuration.Mode == RoutingMode.PartialTraffic)
        {
            foreach (var local in configuration.Rules.Select(rule => rule.LocalAddress).Distinct(StringComparer.Ordinal))
                if (!target.Addresses.Any(address => address.Address == local)) throw new InvalidOperationException($"目标网卡本机地址 {local} 验证失败。");
            foreach (var rule in configuration.Rules)
                if (!target.Routes.Any(route => route.DestinationPrefix == rule.DestinationPrefix)) throw new InvalidOperationException($"部分流量路由 {rule.DestinationPrefix} 验证失败。");
            return;
        }

        if (!target.Ipv4BindingEnabled || !target.Ipv6BindingEnabled) throw new InvalidOperationException("目标网卡的 IPv4/IPv6 未启用。");
        if (!target.Routes.Any(route => route.DestinationPrefix == "0.0.0.0/0")) throw new InvalidOperationException("目标网卡默认路由验证失败。");
        foreach (var original in originalAdapters.Where(adapter => adapter.Id != target.Id))
        {
            var current = refreshed.SingleOrDefault(adapter => adapter.Id == original.Id) ?? throw new InvalidOperationException($"应用后找不到网卡“{original.Name}”。");
            if (current.Ipv4BindingEnabled || current.Ipv6BindingEnabled) throw new InvalidOperationException($"其他网卡“{original.Name}”仍允许 IP 流量。");
        }
    }

    private async Task RestoreSnapshotAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken, bool clearPending = true)
    {
        if (!snapshot.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("快照与当前电脑不匹配，已停止恢复。");
        var current = await _inventory.GetAdaptersAsync(cancellationToken).ConfigureAwait(false);
        var remapped = RemapSnapshot(snapshot, current);
        await _runner.ExecuteAsync(BuildRestoreScript(remapped), cancellationToken).ConfigureAwait(false);
        if (clearPending) _snapshots.ClearPending();
    }

    private static NetworkSnapshot RemapSnapshot(NetworkSnapshot snapshot, IReadOnlyList<AdapterInfo> current)
    {
        var remapped = new List<AdapterSnapshot>();
        foreach (var adapter in snapshot.Adapters)
        {
            var active = current.SingleOrDefault(item => item.Id == adapter.Id)
                ?? throw new InvalidOperationException($"找不到快照中的网卡“{adapter.Name}”，请重新连接后再恢复。");
            remapped.Add(adapter with { InterfaceIndex = active.InterfaceIndex });
        }
        return snapshot with { Adapters = remapped };
    }

    private static NetworkSnapshot RemapSnapshotToAvailableAdapters(NetworkSnapshot snapshot, IReadOnlyList<AdapterInfo> current)
    {
        var currentById = current.ToDictionary(adapter => adapter.Id);
        return snapshot with
        {
            Adapters = snapshot.Adapters
                .Where(adapter => currentById.ContainsKey(adapter.Id))
                .Select(adapter => adapter with { InterfaceIndex = currentById[adapter.Id].InterfaceIndex })
                .ToArray()
        };
    }

    private static IReadOnlyList<AdapterInfo> ModelRestoredAdapters(IReadOnlyList<AdapterInfo> current, NetworkSnapshot baseline)
    {
        var snapshots = baseline.Adapters.ToDictionary(adapter => adapter.Id);
        return current.Select(adapter =>
        {
            if (!snapshots.TryGetValue(adapter.Id, out var snapshot)) return adapter;
            var routes = snapshot.Routes;
            // Older v3 snapshots omitted routes whose protocol was reported as DHCP.
            // A DHCP restore recreates them, so retain the currently observed default route
            // for validation and preference generation when the snapshot has none.
            if (snapshot.DhcpEnabled && !routes.Any(route => route.DestinationPrefix == "0.0.0.0/0"))
            {
                routes = routes.Concat(adapter.Routes.Where(route => route.DestinationPrefix == "0.0.0.0/0")).ToArray();
            }
            return adapter with
            {
                InterfaceIndex = snapshot.InterfaceIndex,
                DhcpEnabled = snapshot.DhcpEnabled,
                AutomaticMetric = snapshot.AutomaticMetric,
                InterfaceMetric = snapshot.InterfaceMetric,
                Ipv4BindingEnabled = snapshot.Ipv4BindingEnabled,
                Ipv6BindingEnabled = snapshot.Ipv6BindingEnabled,
                DnsAutomatic = snapshot.DnsAutomatic ?? snapshot.DhcpEnabled,
                Addresses = snapshot.Addresses,
                DnsServers = snapshot.DnsServers,
                Routes = routes
            };
        }).ToArray();
    }

    private static bool HasManagedDifference(AdapterInfo current, AdapterSnapshot baseline)
    {
        if (current.Ipv4BindingEnabled != baseline.Ipv4BindingEnabled
            || current.Ipv6BindingEnabled != baseline.Ipv6BindingEnabled
            || current.DhcpEnabled != baseline.DhcpEnabled
            || current.DnsAutomatic != (baseline.DnsAutomatic ?? baseline.DhcpEnabled)
            || current.AutomaticMetric != baseline.AutomaticMetric
            || (!baseline.AutomaticMetric && current.InterfaceMetric != baseline.InterfaceMetric)) return true;

        // DHCP leases, gateways and DNS may legitimately renew while DualNet is running.
        // When both sides still use DHCP, they are system-managed and must not trigger a restore.
        if (current.DhcpEnabled && baseline.DhcpEnabled)
        {
            var currentCustomRoutes = current.Routes.Where(IsRestorableRoute)
                .Where(route => route.DestinationPrefix != "0.0.0.0/0")
                .Select(RouteKey).OrderBy(value => value, StringComparer.Ordinal);
            var baselineCustomRoutes = baseline.Routes.Where(IsRestorableRoute)
                .Where(route => route.DestinationPrefix != "0.0.0.0/0")
                .Select(RouteKey).OrderBy(value => value, StringComparer.Ordinal);
            return !currentCustomRoutes.SequenceEqual(baselineCustomRoutes, StringComparer.Ordinal);
        }

        var currentAddresses = current.Addresses
            .Where(address => !address.Address.StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => $"{address.Address}/{address.PrefixLength}/{address.SkipAsSource}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var baselineAddresses = baseline.Addresses
            .Where(address => !address.Address.StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => $"{address.Address}/{address.PrefixLength}/{address.SkipAsSource}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!currentAddresses.SequenceEqual(baselineAddresses, StringComparer.Ordinal)) return true;

        var currentDns = (current.DnsServers ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).OrderBy(value => value, StringComparer.Ordinal);
        var baselineDns = (baseline.DnsServers ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).OrderBy(value => value, StringComparer.Ordinal);
        if (!currentDns.SequenceEqual(baselineDns, StringComparer.Ordinal)) return true;

        var currentRoutes = current.Routes.Where(IsRestorableRoute).Select(RouteKey).OrderBy(value => value, StringComparer.Ordinal);
        var baselineRoutes = baseline.Routes.Where(IsRestorableRoute).Select(RouteKey).OrderBy(value => value, StringComparer.Ordinal);
        return !currentRoutes.SequenceEqual(baselineRoutes, StringComparer.Ordinal);
    }

    private static bool IsRestorableRoute(RouteInfo route) =>
        !route.Protocol.Equals("Local", StringComparison.OrdinalIgnoreCase)
        && !route.Protocol.Equals("Dhcp", StringComparison.OrdinalIgnoreCase);

    private static string RouteKey(RouteInfo route) => $"{route.DestinationPrefix}|{route.NextHop}|{route.RouteMetric}";

    internal static string BuildPartialApplyScript(
        AdapterInfo target,
        IReadOnlyList<RoutingRule> rules,
        AdapterInfo? internetAdapter = null,
        IReadOnlyList<AdapterInfo>? adapters = null)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop'\n");
        script.AppendLine($"$target={target.InterfaceIndex}");
        var existing = target.Addresses.Select(address => address.Address).ToHashSet(StringComparer.Ordinal);
        var addressesToAdd = rules.Select(rule => rule.LocalAddress).Distinct(StringComparer.Ordinal).Where(local => !existing.Contains(local)).ToArray();
        foreach (var local in addressesToAdd)
            script.AppendLine($"New-NetIPAddress -InterfaceIndex $target -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(local)} -PrefixLength {AddedAddressPrefixLength} -Type Unicast -SkipAsSource $false | Out-Null");
        if (addressesToAdd.Length > 0 && target.DhcpEnabled)
        {
            script.AppendLine("# Adding a static address disables DHCP. Freeze the current lease as static until baseline restore.");
            foreach (var address in target.Addresses.Where(address => !address.Address.StartsWith("169.254.", StringComparison.Ordinal)))
            {
                var skipAsSource = addressesToAdd.Any(added =>
                    CidrBlock.FromAddress(IPAddress.Parse(added), AddedAddressPrefixLength).Contains(IPAddress.Parse(address.Address)));
                script.AppendLine($"if (-not (Get-NetIPAddress -InterfaceIndex $target -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(address.Address)} -ErrorAction SilentlyContinue)) {{ New-NetIPAddress -InterfaceIndex $target -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(address.Address)} -PrefixLength {address.PrefixLength} -Type Unicast -SkipAsSource:${skipAsSource.ToString().ToLowerInvariant()} | Out-Null }}");
                if (skipAsSource)
                    script.AppendLine($"Set-NetIPAddress -InterfaceIndex $target -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(address.Address)} -SkipAsSource $true -ErrorAction SilentlyContinue");
            }
            var dnsServers = (target.DnsServers ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (dnsServers.Length > 0)
                script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex $target -ServerAddresses @({string.Join(",", dnsServers.Select(PowerShellEscaping.Quote))})");
            foreach (var route in target.Routes.Where(route => !route.Protocol.Equals("Local", StringComparison.OrdinalIgnoreCase)))
                script.AppendLine($"if (-not (Get-NetRoute -InterfaceIndex $target -AddressFamily IPv4 -DestinationPrefix {PowerShellEscaping.Quote(route.DestinationPrefix)} -NextHop {PowerShellEscaping.Quote(route.NextHop)} -ErrorAction SilentlyContinue)) {{ New-NetRoute -InterfaceIndex $target -AddressFamily IPv4 -DestinationPrefix {PowerShellEscaping.Quote(route.DestinationPrefix)} -NextHop {PowerShellEscaping.Quote(route.NextHop)} -RouteMetric {route.RouteMetric} -PolicyStore ActiveStore | Out-Null }}");
        }
        script.AppendLine("Start-Sleep -Milliseconds 900");
        foreach (var local in addressesToAdd)
            script.AppendLine($"if ((Get-NetIPAddress -InterfaceIndex $target -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(local)} -ErrorAction Stop).AddressState -eq 'Duplicate') {{ throw '检测到本机 IP 冲突：{local}' }}");
        foreach (var rule in rules)
            script.AppendLine($"if (-not (Get-NetRoute -InterfaceIndex $target -AddressFamily IPv4 -DestinationPrefix {PowerShellEscaping.Quote(rule.DestinationPrefix)} -ErrorAction SilentlyContinue)) {{ New-NetRoute -InterfaceIndex $target -AddressFamily IPv4 -DestinationPrefix {PowerShellEscaping.Quote(rule.DestinationPrefix)} -NextHop '0.0.0.0' -RouteMetric 1 -PolicyStore ActiveStore | Out-Null }}");
        foreach (var rule in rules)
            script.AppendLine($"if (-not (Get-NetRoute -InterfaceIndex $target -AddressFamily IPv4 -DestinationPrefix {PowerShellEscaping.Quote(rule.DestinationPrefix)} -ErrorAction SilentlyContinue)) {{ throw '目标路由验证失败：{rule.DestinationPrefix}' }}");
        if (internetAdapter is not null)
        {
            script.AppendLine($"$internet={internetAdapter.InterfaceIndex}");
            script.AppendLine("$internetDefaults=@(Get-NetRoute -InterfaceIndex $internet -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)");
            script.AppendLine("if ($internetDefaults.Count -eq 0) { throw '指定的其余流量出口没有默认路由。' }");
            script.AppendLine("Set-NetIPInterface -InterfaceIndex $internet -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1 -ErrorAction Stop");
            script.AppendLine("$internetDefaults | Set-NetRoute -RouteMetric 1 -Confirm:$false -ErrorAction Stop");
            foreach (var other in (adapters ?? []).Where(adapter => adapter.Id != internetAdapter.Id && adapter.HasDefaultRoute))
            {
                script.AppendLine($"Set-NetIPInterface -InterfaceIndex {other.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 5000 -ErrorAction Stop");
                script.AppendLine($"Get-NetRoute -InterfaceIndex {other.InterfaceIndex} -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Set-NetRoute -RouteMetric 5000 -Confirm:$false -ErrorAction Stop");
            }
            script.AppendLine("$routeChoices=@(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object { $ipif=Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1; [pscustomobject]@{ InterfaceIndex=[int]$_.InterfaceIndex; TotalMetric=([int]$_.RouteMetric + [int]$ipif.InterfaceMetric) } })");
            script.AppendLine("$winner=$routeChoices | Sort-Object TotalMetric,InterfaceIndex | Select-Object -First 1");
            script.AppendLine("if ($null -eq $winner -or $winner.InterfaceIndex -ne $internet) { throw '其余流量出口默认路由优先级验证失败。' }");
        }
        return script.ToString();
    }

    internal static string BuildAllTrafficApplyScript(AdapterInfo target, IReadOnlyList<AdapterInfo> adapters, AllTrafficSettings settings)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop'\n");
        script.AppendLine($"$target={target.InterfaceIndex}");
        script.AppendLine("$targetDescription=(Get-NetAdapter -InterfaceIndex $target -ErrorAction Stop).InterfaceDescription");
        script.AppendLine("Enable-NetAdapterBinding -InterfaceDescription $targetDescription -ComponentID ms_tcpip -Confirm:$false -ErrorAction Stop | Out-Null");
        script.AppendLine("Enable-NetAdapterBinding -InterfaceDescription $targetDescription -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop | Out-Null");
        if (!target.Addresses.Any(address => address.Address == settings.LocalAddress && address.PrefixLength == settings.PrefixLength))
        {
            script.AppendLine("Set-NetIPInterface -InterfaceIndex $target -AddressFamily IPv4 -Dhcp Disabled -ErrorAction Stop");
            script.AppendLine($"New-NetIPAddress -InterfaceIndex $target -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(settings.LocalAddress)} -PrefixLength {settings.PrefixLength} -Type Unicast -SkipAsSource $false | Out-Null");
        }
        if (settings.DnsServers.Count > 0)
            script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex $target -ServerAddresses @({string.Join(",", settings.DnsServers.Select(PowerShellEscaping.Quote))})");
        script.AppendLine("Set-NetIPInterface -InterfaceIndex $target -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1");
        script.AppendLine("Set-NetIPInterface -InterfaceIndex $target -AddressFamily IPv6 -AutomaticMetric Disabled -InterfaceMetric 1 -ErrorAction SilentlyContinue");
        script.AppendLine("$defaults=@(Get-NetRoute -InterfaceIndex $target -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)");
        script.AppendLine($"$matching=@($defaults | Where-Object {{ $_.NextHop -eq {PowerShellEscaping.Quote(settings.Gateway)} }})");
        script.AppendLine($"if ($matching.Count -gt 0) {{ $matching | Set-NetRoute -RouteMetric 1 -Confirm:$false }} else {{ $defaults | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; New-NetRoute -InterfaceIndex $target -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -NextHop {PowerShellEscaping.Quote(settings.Gateway)} -RouteMetric 1 -PolicyStore ActiveStore | Out-Null }}");
        foreach (var adapter in adapters.Where(adapter => adapter.Id != target.Id))
        {
            script.AppendLine($"$description=(Get-NetAdapter -InterfaceIndex {adapter.InterfaceIndex} -ErrorAction Stop).InterfaceDescription");
            script.AppendLine("Disable-NetAdapterBinding -InterfaceDescription $description -ComponentID ms_tcpip -Confirm:$false -ErrorAction Stop | Out-Null");
            script.AppendLine("Disable-NetAdapterBinding -InterfaceDescription $description -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop | Out-Null");
        }
        return script.ToString();
    }

    internal static string BuildRestoreScript(NetworkSnapshot snapshot)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop'\n");
        foreach (var adapter in snapshot.Adapters)
        {
            script.AppendLine($"$description=(Get-NetAdapter -InterfaceIndex {adapter.InterfaceIndex} -ErrorAction Stop).InterfaceDescription");
            script.AppendLine("Enable-NetAdapterBinding -InterfaceDescription $description -ComponentID ms_tcpip -Confirm:$false -ErrorAction SilentlyContinue | Out-Null");
            script.AppendLine("Enable-NetAdapterBinding -InterfaceDescription $description -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null");
        }
        script.AppendLine("Start-Sleep -Milliseconds 600");
        foreach (var adapter in snapshot.Adapters)
        {
            var index = adapter.InterfaceIndex;
            script.AppendLine($"Get-NetRoute -InterfaceIndex {index} -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {{ $_.Protocol -ne 'Local' }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
            script.AppendLine($"Get-NetIPAddress -InterfaceIndex {index} -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {{ $_.IPAddress -notlike '169.254.*' }} | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue");
            script.AppendLine($"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -Dhcp {(adapter.DhcpEnabled ? "Enabled" : "Disabled")} -ErrorAction SilentlyContinue");
            foreach (var address in adapter.Addresses.Where(value => !value.Address.StartsWith("169.254.", StringComparison.Ordinal)
                         && (!adapter.DhcpEnabled || !value.PrefixOrigin.Equals("Dhcp", StringComparison.OrdinalIgnoreCase))))
                script.AppendLine($"New-NetIPAddress -InterfaceIndex {index} -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(address.Address)} -PrefixLength {address.PrefixLength} -Type Unicast -SkipAsSource:${address.SkipAsSource.ToString().ToLowerInvariant()} -ErrorAction SilentlyContinue | Out-Null");
            var dnsServers = (adapter.DnsServers ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var dnsAutomatic = adapter.DnsAutomatic ?? adapter.DhcpEnabled;
            if (dnsAutomatic || dnsServers.Length == 0)
                script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {index} -ResetServerAddresses -ErrorAction SilentlyContinue");
            else
                script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses @({string.Join(",", dnsServers.Select(PowerShellEscaping.Quote))}) -ErrorAction SilentlyContinue");
            script.AppendLine(adapter.AutomaticMetric
                ? $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction SilentlyContinue"
                : $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {adapter.InterfaceMetric} -ErrorAction SilentlyContinue");
            foreach (var route in adapter.Routes.Where(route => !route.Protocol.Equals("Local", StringComparison.OrdinalIgnoreCase) && !route.Protocol.Equals("Dhcp", StringComparison.OrdinalIgnoreCase)))
                script.AppendLine($"New-NetRoute -InterfaceIndex {index} -AddressFamily IPv4 -DestinationPrefix {PowerShellEscaping.Quote(route.DestinationPrefix)} -NextHop {PowerShellEscaping.Quote(route.NextHop)} -RouteMetric {route.RouteMetric} -ErrorAction SilentlyContinue | Out-Null");
        }
        foreach (var adapter in snapshot.Adapters)
        {
            script.AppendLine($"$description=(Get-NetAdapter -InterfaceIndex {adapter.InterfaceIndex} -ErrorAction Stop).InterfaceDescription");
            if (!adapter.Ipv4BindingEnabled)
                script.AppendLine("Disable-NetAdapterBinding -InterfaceDescription $description -ComponentID ms_tcpip -Confirm:$false -ErrorAction SilentlyContinue | Out-Null");
            if (!adapter.Ipv6BindingEnabled)
                script.AppendLine("Disable-NetAdapterBinding -InterfaceDescription $description -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction SilentlyContinue | Out-Null");
        }
        return script.ToString();
    }

    internal static string BuildPartialResetScript(IReadOnlyList<AdapterInfo> currentAdapters, AdapterInfo target,
        AdapterInfo? internet, IReadOnlyList<RoutingRule> rules, NetworkSnapshot? baseline)
    {
        var script = new StringBuilder("$ErrorActionPreference='Stop'\n");
        var snapshots = (baseline?.Adapters ?? []).ToDictionary(adapter => adapter.Id);
        var routeMetricRestores = new List<string>();
        foreach (var adapter in currentAdapters)
        {
            if (!snapshots.TryGetValue(adapter.Id, out var original)) continue;
            if (adapter.AutomaticMetric != original.AutomaticMetric
                || (!original.AutomaticMetric && adapter.InterfaceMetric != original.InterfaceMetric))
                script.AppendLine(original.AutomaticMetric
                    ? $"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop"
                    : $"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {original.InterfaceMetric} -ErrorAction Stop");
            foreach (var route in adapter.Routes.Where(route => route.DestinationPrefix == "0.0.0.0/0"))
            {
                var oldRoute = original.Routes.FirstOrDefault(old => old.DestinationPrefix == route.DestinationPrefix && old.NextHop == route.NextHop);
                if (oldRoute is not null && oldRoute.RouteMetric != route.RouteMetric)
                    routeMetricRestores.Add($"Get-NetRoute -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -NextHop {PowerShellEscaping.Quote(route.NextHop)} -ErrorAction SilentlyContinue | Set-NetRoute -RouteMetric {oldRoute.RouteMetric} -Confirm:$false -ErrorAction Stop");
            }
        }

        if (snapshots.TryGetValue(target.Id, out var targetBaseline))
        {
            var baselineAddresses = targetBaseline.Addresses.Select(address => address.Address).ToHashSet(StringComparer.Ordinal);
            foreach (var local in rules.Select(rule => rule.LocalAddress).Distinct(StringComparer.Ordinal).Where(local => !baselineAddresses.Contains(local)))
                script.AppendLine($"Get-NetIPAddress -InterfaceIndex {target.InterfaceIndex} -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(local)} -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop");
            foreach (var lease in targetBaseline.Addresses.Where(address => address.PrefixOrigin.Equals("Dhcp", StringComparison.OrdinalIgnoreCase)))
                script.AppendLine($"Get-NetIPAddress -InterfaceIndex {target.InterfaceIndex} -AddressFamily IPv4 -IPAddress {PowerShellEscaping.Quote(lease.Address)} -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop");
        }

        snapshots.TryGetValue(target.Id, out targetBaseline);
        foreach (var rule in rules.Select(rule => rule.DestinationPrefix).Distinct(StringComparer.Ordinal))
        {
            var existed = targetBaseline?.Routes.Any(route => route.DestinationPrefix == rule && route.NextHop == "0.0.0.0") == true;
            if (!existed)
                script.AppendLine($"Get-NetRoute -InterfaceIndex {target.InterfaceIndex} -AddressFamily IPv4 -DestinationPrefix {PowerShellEscaping.Quote(rule)} -NextHop '0.0.0.0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction Stop");
        }

        script.AppendLine("Start-Sleep -Milliseconds 900");
        script.Append(BuildEnableDhcpScript(target, snapshots.GetValueOrDefault(target.Id)));
        if (internet is not null) script.Append(BuildEnableDhcpScript(internet, snapshots.GetValueOrDefault(internet.Id)));
        script.AppendLine("Start-Sleep -Milliseconds 900");
        foreach (var restore in routeMetricRestores) script.AppendLine(restore);
        return script.ToString();
    }

    private static AdapterSnapshot? FindSnapshot(NetworkSnapshot snapshot, Guid adapterId) =>
        snapshot.Adapters.SingleOrDefault(adapter => adapter.Id == adapterId);

    private static string BuildEnableDhcpScript(AdapterInfo adapter, AdapterSnapshot? baseline)
    {
        var script = new StringBuilder();
        script.AppendLine($"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop");
        var dnsAutomatic = baseline?.DnsAutomatic ?? baseline?.DhcpEnabled ?? adapter.DnsAutomatic;
        var servers = (baseline?.DnsServers ?? adapter.DnsServers).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (dnsAutomatic || servers.Length == 0)
            script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {adapter.InterfaceIndex} -ResetServerAddresses -ErrorAction Stop");
        else
        {
            script.AppendLine($"Set-DnsClientServerAddress -InterfaceIndex {adapter.InterfaceIndex} -ServerAddresses @({string.Join(",", servers.Select(PowerShellEscaping.Quote))}) -ErrorAction Stop");
        }
        return script.ToString();
    }
}
