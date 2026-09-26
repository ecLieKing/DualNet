using System.Net;
using System.Text.Json;
using DualNet.Core.Models;
using DualNet.Core.Networking;
using DualNet.Core.Services;

namespace DualNet.Tests;

[TestClass]
public sealed class NetworkConfigurationTests
{
    [TestMethod]
    public async Task PartialTrafficCanPreferUsbInternetAdapterWithoutConnectivityProbe()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = CreateAdapter("WLAN", 15, true, [new IpAddressInfo("192.168.40.36", 24, "Dhcp", "Dhcp", false)]);
            var usb = CreateAdapter("USB 网络共享", 50, true, [new IpAddressInfo("10.64.10.203", 24, "Dhcp", "Dhcp", false)]);
            var appliedTarget = target with { Routes = target.Routes.Concat([new RouteInfo("192.168.40.1/32", "0.0.0.0", 1, "NetMgmt", false)]).ToArray() };
            var runner = new SuccessRunner([appliedTarget, usb]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), new SnapshotStore(root), new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.40.1", out var rule, out _);

            var result = await service.ApplyAsync(new RouteConfiguration
            {
                TargetAdapterId = target.Id,
                InternetAdapterId = usb.Id,
                Mode = RoutingMode.PartialTraffic,
                Rules = [rule!]
            }, [target, usb]);

            Assert.AreEqual(usb.Id, result.Configuration.InternetAdapterId);
            StringAssert.Contains(runner.ApplyScript, "$internet=50");
            StringAssert.Contains(runner.ApplyScript, "InterfaceIndex $internet -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 1");
            StringAssert.Contains(runner.ApplyScript, "InterfaceIndex 15 -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 5000");
            StringAssert.Contains(runner.ApplyScript, "$winner.InterfaceIndex -ne $internet");
            Assert.IsFalse(runner.ApplyScript.Contains("Invoke-WebRequest", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(runner.ApplyScript.Contains("Test-NetConnection", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OlderBaselineWithoutDhcpDefaultRouteCanStillSelectCurrentUsbInternet()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = CreateAdapter("WLAN", 15, true, [new IpAddressInfo("192.168.40.36", 24, "Dhcp", "Dhcp", false)]);
            var usb = CreateAdapter("USB 网络共享", 50, true, [new IpAddressInfo("10.64.10.203", 24, "Dhcp", "Dhcp", false)]) with
            {
                Routes = [new RouteInfo("0.0.0.0/0", "10.64.10.178", 0, "Dhcp", false)]
            };
            var currentlyApplied = target with
            {
                Routes = target.Routes.Concat([new RouteInfo("192.168.40.1/32", "0.0.0.0", 1, "NetMgmt", false)]).ToArray()
            };
            var store = new SnapshotStore(root);
            await store.SaveBaselineIfMissingAsync(new NetworkSnapshot
            {
                Version = 3, MachineName = Environment.MachineName, CreatedAt = DateTimeOffset.Now,
                TargetAdapterId = target.Id,
                Adapters = [AdapterSnapshot.From(target), AdapterSnapshot.From(usb) with { Routes = [] }]
            });
            var reapplied = currentlyApplied;
            var runner = new SuccessRunner([reapplied, usb], [currentlyApplied, usb]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), store, new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.40.1", out var rule, out _);

            var result = await service.ApplyAsync(new RouteConfiguration
            {
                TargetAdapterId = target.Id, InternetAdapterId = usb.Id,
                Mode = RoutingMode.PartialTraffic, Rules = [rule!]
            }, [currentlyApplied, usb]);

            Assert.AreEqual(usb.Id, result.Configuration.InternetAdapterId);
            StringAssert.Contains(runner.ApplyScript, "$internet=50");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DisconnectedInternetAdapterIsAllowedWhenItHasDefaultRoute()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = CreateAdapter("目标", 2, false, [new IpAddressInfo("192.168.10.88", 24, "Manual", "Manual", false)]);
            var internet = CreateAdapter("断开的 USB", 50, true, [new IpAddressInfo("10.0.0.9", 24, "Dhcp", "Dhcp", false)], status: "Disconnected");
            var applied = target with { Routes = [new RouteInfo("192.168.10.6/32", "0.0.0.0", 1, "NetMgmt", false)] };
            var runner = new SuccessRunner([applied, internet]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), new SnapshotStore(root), new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);

            var result = await service.ApplyAsync(new RouteConfiguration
            {
                TargetAdapterId = target.Id, InternetAdapterId = internet.Id,
                Mode = RoutingMode.PartialTraffic, Rules = [rule!]
            }, [target, internet]);

            Assert.AreEqual(internet.Id, result.Configuration.InternetAdapterId);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InternetAdapterMustBeDifferentAndHaveDefaultRoute()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = CreateAdapter("目标", 2, false, [new IpAddressInfo("192.168.10.88", 24, "Manual", "Manual", false)]);
            var noDefault = CreateAdapter("无网关 USB", 50, false, [new IpAddressInfo("10.0.0.9", 24, "Manual", "Manual", false)]);
            var service = new NetworkConfigurationService(new SuccessRunner([target, noDefault]), new NetworkInventoryService(new SuccessRunner([target, noDefault])), new SnapshotStore(root), new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ApplyAsync(new RouteConfiguration
            {
                TargetAdapterId = target.Id, InternetAdapterId = target.Id,
                Mode = RoutingMode.PartialTraffic, Rules = [rule!]
            }, [target, noDefault]));
            var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ApplyAsync(new RouteConfiguration
            {
                TargetAdapterId = target.Id, InternetAdapterId = noDefault.Id,
                Mode = RoutingMode.PartialTraffic, Rules = [rule!]
            }, [target, noDefault]));
            StringAssert.Contains(exception.Message, "没有默认路由");
        }
        finally { Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow("以太网", "NdisPhysicalMedium802_3", true, true)]
    [DataRow("Wi-Fi", "NdisPhysicalMediumNative802_11", true, true)]
    [DataRow("USB 蜂窝网卡", "NdisPhysicalMediumWirelessWan", true, true)]
    [DataRow("公司 VPN", "", false, true)]
    [DataRow("USB 网络适配器", "NdisPhysicalMedium802_3", true, true)]
    [DataRow("VMware 虚拟网卡", "", false, true)]
    [DataRow("未连接 Wi-Fi", "NdisPhysicalMediumNative802_11", true, false)]
    public async Task PartialTrafficAcceptsEveryVisibleAdapterType(string name, string media, bool physical, bool connected)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var id = Guid.NewGuid();
            var status = connected ? "Up" : "Disconnected";
            var target = CreateAdapter(name, 7, false, [new IpAddressInfo("192.168.10.88", 24, "Manual", "Manual", false)], id,
                status: status, media: media, physical: physical);
            var applied = target with { Routes = [new RouteInfo("192.168.10.6/32", "0.0.0.0", 1, "NetMgmt", false)] };
            var runner = new SuccessRunner([applied], [target]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), new SnapshotStore(root), new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);

            var result = await service.ApplyAsync(
                new RouteConfiguration { TargetAdapterId = id, Mode = RoutingMode.PartialTraffic, Rules = [rule!] }, [target]);

            Assert.AreEqual(id, result.Configuration.TargetAdapterId);
            StringAssert.Contains(runner.ApplyScript, "'192.168.10.6/32'");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PartialTrafficWorksWithoutUplinkDefaultRouteOrLink()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var targetId = Guid.NewGuid();
            var target = CreateAdapter("离线以太网", 2, false, [], targetId, status: "Disconnected");
            var other = CreateAdapter("WLAN", 12, false, [new IpAddressInfo("10.0.0.5", 24, "Manual", "Manual", false)]);
            var applied = CreateAdapter("离线以太网", 2, false, [new IpAddressInfo("192.168.10.200", 24, "Manual", "Manual", false)], targetId,
                [new RouteInfo("192.168.10.6/32", "0.0.0.0", 1, "NetMgmt", false)], status: "Disconnected");
            var runner = new SuccessRunner([applied, other], [target, other]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), new SnapshotStore(root), new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);
            var configuration = new RouteConfiguration { TargetAdapterId = targetId, Mode = RoutingMode.PartialTraffic, Rules = [rule!] };

            var result = await service.ApplyAsync(configuration, [target, other]);

            Assert.AreEqual("192.168.10.200", result.Configuration.Rules.Single().LocalAddress);
            Assert.IsNull(result.ConfirmationOperationId);
            StringAssert.Contains(runner.ApplyScript, "-PrefixLength 24");
            StringAssert.Contains(runner.ApplyScript, "Freeze the current lease as static");
            var partialApply = runner.ApplyScript[runner.ApplyScript.LastIndexOf("$target=", StringComparison.Ordinal)..];
            Assert.IsFalse(partialApply.Contains("-Dhcp Enabled", StringComparison.Ordinal));
            StringAssert.Contains(runner.ApplyScript, "'192.168.10.6/32'");
            Assert.IsFalse(runner.ApplyScript.Contains("0.0.0.0/0", StringComparison.Ordinal));
            Assert.IsFalse(runner.RestoreBeforeApply, "首次应用不应重置其他网卡");
            Assert.IsFalse(new SnapshotStore(root).HasPendingOperation);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PartialTrafficReusesExistingMatchingAddressWithoutChangingIp()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var id = Guid.NewGuid();
            var current = CreateAdapter("目标", 3, false, [new IpAddressInfo("192.168.10.88", 24, "Dhcp", "Dhcp", false)], id);
            var applied = current with { Routes = [new RouteInfo("192.168.10.6/32", "0.0.0.0", 1, "NetMgmt", false)] };
            var runner = new SuccessRunner([applied], [current]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), new SnapshotStore(root), new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);

            var result = await service.ApplyAsync(new RouteConfiguration { TargetAdapterId = id, Mode = RoutingMode.PartialTraffic, Rules = [rule!] }, [current]);

            Assert.AreEqual("192.168.10.88", result.Configuration.Rules.Single().LocalAddress);
            Assert.IsFalse(runner.ApplyScript.Contains("New-NetIPAddress", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ExistingV3BaselineIsRestoredBeforeRouteUpdate()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var id = Guid.NewGuid();
            var original = CreateAdapter("目标", 2, false, [], id);
            var currentlyApplied = CreateAdapter("目标", 2, false, [new IpAddressInfo("192.168.10.200", 24, "Manual", "Manual", false)], id,
                [new RouteInfo("192.168.10.6/32", "0.0.0.0", 1, "NetMgmt", false)]);
            var reapplied = currentlyApplied;
            var store = new SnapshotStore(root);
            var createdAt = DateTimeOffset.Parse("2026-08-07T00:00:00+08:00");
            await store.SaveBaselineIfMissingAsync(new NetworkSnapshot
            {
                Version = 3, MachineName = Environment.MachineName, CreatedAt = createdAt,
                TargetAdapterId = id, Adapters = [AdapterSnapshot.From(original) with { DnsServers = [null!] }]
            });
            var runner = new SuccessRunner([reapplied], [currentlyApplied]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), store, new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);

            await service.ApplyAsync(new RouteConfiguration { TargetAdapterId = id, Mode = RoutingMode.PartialTraffic, Rules = [rule!] }, [currentlyApplied]);

            Assert.IsTrue(runner.RestoreBeforeApply);
            Assert.AreEqual(createdAt, (await store.LoadBaselineAsync())!.CreatedAt);
            Assert.IsFalse(runner.RestoreScript.Contains("ServerAddresses @('')", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UsbSharingScenarioCanChangeLocalAddressWhenAnotherAdapterHasNullDns()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var targetId = Guid.NewGuid();
            var virtualId = Guid.NewGuid();
            var usbId = Guid.NewGuid();
            var target = CreateAdapter("WLAN", 15, true, [new IpAddressInfo("192.168.40.36", 24, "Dhcp", "Dhcp", false)], targetId) with
            {
                DnsServers = ["192.168.40.1"]
            };
            var virtualAdapter = CreateAdapter("vEthernet (FSE HostVnic)", 58, false, [], virtualId, physical: false) with
            {
                DhcpEnabled = false,
                DnsServers = [null!]
            };
            var usbBaseline = CreateAdapter("USB 网络共享", 50, true, [new IpAddressInfo("10.64.10.147", 24, "Dhcp", "Dhcp", false)], usbId) with
            {
                DnsServers = ["10.64.10.178"]
            };
            var usbCurrent = usbBaseline with
            {
                Addresses = [new IpAddressInfo("10.64.10.203", 24, "Dhcp", "Dhcp", false)]
            };
            var currentlyApplied = target with
            {
                Routes = target.Routes.Concat([new RouteInfo("192.168.40.1/32", "0.0.0.0", 1, "NetMgmt", false)]).ToArray()
            };
            var appliedTarget = target with
            {
                Addresses = target.Addresses.Concat([new IpAddressInfo("192.168.40.114", 24, "Manual", "Manual", false)]).ToArray(),
                Routes = target.Routes.Concat([new RouteInfo("192.168.40.1/32", "0.0.0.0", 1, "NetMgmt", false)]).ToArray()
            };
            var store = new SnapshotStore(root);
            await store.SaveBaselineIfMissingAsync(new NetworkSnapshot
            {
                Version = 3, MachineName = Environment.MachineName, CreatedAt = DateTimeOffset.Now,
                TargetAdapterId = targetId,
                Adapters =
                [
                    AdapterSnapshot.From(target),
                    AdapterSnapshot.From(virtualAdapter) with { DnsServers = [null!] },
                    AdapterSnapshot.From(usbBaseline)
                ]
            });
            var runner = new SuccessRunner(
                [appliedTarget, virtualAdapter with { DnsServers = [] }, usbCurrent],
                [currentlyApplied, virtualAdapter with { DnsServers = [] }, usbCurrent]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), store, new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.40.1", out var parsed, out _);
            var edited = parsed! with { LocalAddress = "192.168.40.114", IsLocalAddressManual = true };
            var progress = new InlineProgress<string>();

            var result = await service.ApplyAsync(
                new RouteConfiguration { TargetAdapterId = targetId, Mode = RoutingMode.PartialTraffic, Rules = [edited] },
                [currentlyApplied, virtualAdapter, usbCurrent], progress: progress);

            Assert.AreEqual("192.168.40.114", result.Configuration.Rules.Single().LocalAddress);
            Assert.IsTrue(runner.RestoreBeforeApply);
            Assert.IsFalse(runner.RestoreScript.Contains("InterfaceIndex 58", StringComparison.Ordinal), "未修改的 Hyper-V 网卡不应在更新时重写");
            Assert.IsFalse(runner.RestoreScript.Contains("InterfaceIndex 50", StringComparison.Ordinal), "正常续租的 USB 共享网卡不应在更新时重写");
            StringAssert.Contains(runner.ApplyScript, "'192.168.40.114'");
            StringAssert.Contains(runner.ApplyScript, "Set-NetIPAddress");
            StringAssert.Contains(runner.ApplyScript, "-SkipAsSource $true");
            StringAssert.Contains(runner.ApplyScript, "'192.168.40.1'");
            StringAssert.Contains(runner.ApplyScript, "Set-DnsClientServerAddress");
            var partialUpdate = runner.ApplyScript[runner.ApplyScript.LastIndexOf("$target=", StringComparison.Ordinal)..];
            Assert.IsFalse(partialUpdate.Contains("-Dhcp Enabled", StringComparison.Ordinal));
            Assert.IsFalse(new SnapshotStore(root).HasPendingOperation);
            Assert.AreEqual(1, runner.InventoryCallCount, "部分流量更新只应查询一次当前路由状态");
            CollectionAssert.IsSubsetOf(
                new[] { "正在保存网络恢复点…", "正在读取当前路由状态…", "正在准备原始网卡配置…", "正在检查本机地址…", "正在写入地址和路由…", "正在确认路由结果…", "路由已生效。" },
                progress.Values);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AllTrafficResolvesExistingAddressGatewayAndDns()
    {
        var adapter = CreateAdapter("WLAN", 12, true, [new IpAddressInfo("10.64.20.9", 22, "Dhcp", "Dhcp", false)]) with
        {
            DnsServers = ["223.5.5.5"]
        };

        var result = NetworkConfigurationService.ResolveAllTrafficSettings(adapter, new AllTrafficSettings());

        Assert.AreEqual("10.64.20.9", result.LocalAddress);
        Assert.AreEqual(22, result.PrefixLength);
        Assert.AreEqual("10.64.20.1", result.Gateway);
    }

    [TestMethod]
    public void AllTrafficRejectsGatewayOutsideLocalSubnet()
    {
        var adapter = CreateAdapter("目标", 2, false, []);
        var settings = new AllTrafficSettings { LocalAddress = "192.168.10.20", PrefixLength = 24, Gateway = "10.0.0.1" };
        Assert.ThrowsException<InvalidOperationException>(() => NetworkConfigurationService.ResolveAllTrafficSettings(adapter, settings));
    }

    [TestMethod]
    public void AllTrafficRejectsInvalidOptionalDns()
    {
        var adapter = CreateAdapter("目标", 2, false, []);
        var settings = new AllTrafficSettings
        {
            LocalAddress = "192.168.10.20", PrefixLength = 24, Gateway = "192.168.10.1", DnsServers = ["not-an-ip"]
        };
        Assert.ThrowsException<InvalidOperationException>(() => NetworkConfigurationService.ResolveAllTrafficSettings(adapter, settings));
    }

    [TestMethod]
    public async Task ActiveV2BaselineMustBeRestoredBeforeV12Apply()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = CreateAdapter("目标", 2, false, []);
            var store = new SnapshotStore(root);
            await store.SaveBaselineIfMissingAsync(new NetworkSnapshot
            {
                Version = 2, MachineName = Environment.MachineName, CreatedAt = DateTimeOffset.Now,
                IndustrialAdapterId = target.Id, UplinkAdapterId = Guid.NewGuid(), Adapters = [AdapterSnapshot.From(target)]
            });
            var runner = new SuccessRunner([target]);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), store, new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);

            var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ApplyAsync(
                new RouteConfiguration { TargetAdapterId = target.Id, Mode = RoutingMode.PartialTraffic, Rules = [rule!] }, [target]));

            StringAssert.Contains(exception.Message, "先点击“恢复原网络”");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task StrictAllTrafficDisablesBothProtocolsAndWaitsForConfirmation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var targetId = Guid.NewGuid();
            var otherId = Guid.NewGuid();
            var target = CreateAdapter("WLAN", 12, true, [new IpAddressInfo("10.64.20.9", 22, "Dhcp", "Dhcp", false)], targetId);
            var other = CreateAdapter("VMware", 18, false, [new IpAddressInfo("192.168.20.1", 24, "Manual", "Manual", false)], otherId);
            var appliedTarget = target with { Ipv4BindingEnabled = true, Ipv6BindingEnabled = true };
            var appliedOther = other with { Ipv4BindingEnabled = false, Ipv6BindingEnabled = false, Addresses = [], Routes = [] };
            var runner = new SuccessRunner([appliedTarget, appliedOther], [target, other]);
            var store = new SnapshotStore(root);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), store, new FakeProbe());
            var started = Guid.Empty;
            var configuration = new RouteConfiguration { TargetAdapterId = targetId, Mode = RoutingMode.AllTraffic };

            var result = await service.ApplyAsync(configuration, [target, other], id => { started = id; return Task.CompletedTask; });

            Assert.AreEqual(result.ConfirmationOperationId, started);
            Assert.IsTrue(store.HasPendingOperation);
            Assert.IsTrue(store.IsWatchdogArmed(started));
            StringAssert.Contains(runner.ApplyScript, "-ComponentID ms_tcpip ");
            StringAssert.Contains(runner.ApplyScript, "-ComponentID ms_tcpip6 ");
            StringAssert.Contains(runner.ApplyScript, "'0.0.0.0/0'");
            StringAssert.Contains(runner.ApplyScript, "$matching");

            await service.ConfirmStrictConfigurationAsync(started);
            Assert.IsFalse(store.HasPendingOperation);
            Assert.IsTrue(store.IsWatchdogConfirmed(started));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ApplyFailureRollsBackAndPreservesV3Baseline()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var target = CreateAdapter("目标", 2, false, []);
            var runner = new FailingApplyRunner([target]);
            var store = new SnapshotStore(root);
            var service = new NetworkConfigurationService(runner, new NetworkInventoryService(runner), store, new FakeProbe());
            RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var rule, out _);
            var configuration = new RouteConfiguration { TargetAdapterId = target.Id, Mode = RoutingMode.PartialTraffic, Rules = [rule!] };

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ApplyAsync(configuration, [target]));

            Assert.IsTrue(runner.RestoreWasAttempted);
            var baseline = await store.LoadBaselineAsync();
            Assert.AreEqual(3, baseline!.Version);
            Assert.AreEqual(1, baseline.Adapters.Count);
            Assert.IsFalse(store.HasPendingOperation);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestoreScriptEnablesBindingsBeforeNetworkSettingsAndRestoresDisabledState()
    {
        var adapter = CreateAdapter("VPN", 9, false, []) with { Ipv4BindingEnabled = false, Ipv6BindingEnabled = false };
        var snapshot = new NetworkSnapshot
        {
            Version = 3, MachineName = Environment.MachineName, CreatedAt = DateTimeOffset.Now,
            TargetAdapterId = adapter.Id, Adapters = [AdapterSnapshot.From(adapter)]
        };

        var script = NetworkConfigurationService.BuildRestoreScript(snapshot);

        var firstEnable = script.IndexOf("Enable-NetAdapterBinding", StringComparison.Ordinal);
        var ipRestore = script.IndexOf("Set-NetIPInterface", StringComparison.Ordinal);
        var lastDisable = script.LastIndexOf("Disable-NetAdapterBinding", StringComparison.Ordinal);
        Assert.IsTrue(firstEnable >= 0 && firstEnable < ipRestore && lastDisable > ipRestore);
    }

    [TestMethod]
    public void PowerShellQuoteEscapesSingleQuote() => Assert.AreEqual("'a''b'", PowerShellEscaping.Quote("a'b"));

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DualNetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static AdapterInfo CreateAdapter(string name, int index, bool defaultRoute, IReadOnlyList<IpAddressInfo> addresses, Guid? id = null,
        IReadOnlyList<RouteInfo>? additionalRoutes = null, string status = "Up", string? media = null, bool? physical = null) => new()
    {
        Id = id ?? Guid.NewGuid(), InterfaceIndex = index, Name = name, Description = name, Status = status,
        PhysicalMediaType = media ?? (name == "WLAN" ? "NdisPhysicalMediumNative802_11" : "NdisPhysicalMedium802_3"),
        IsPhysical = physical ?? !name.Contains("VMware"),
        Ipv4BindingEnabled = true, Ipv6BindingEnabled = true, DhcpEnabled = true, AutomaticMetric = true, InterfaceMetric = 25,
        Addresses = addresses, DnsServers = [],
        Routes = (defaultRoute ? new[] { new RouteInfo("0.0.0.0/0", "10.64.20.1", 0, "Dhcp", false) } : []).Concat(additionalRoutes ?? []).ToArray()
    };

    private static string ToInventoryJson(IEnumerable<AdapterInfo> source) => JsonSerializer.Serialize(source.Select(adapter => new
    {
        Id = adapter.Id.ToString(), adapter.InterfaceIndex, adapter.Name, adapter.Description, adapter.Status, adapter.PhysicalMediaType,
        adapter.IsPhysical, adapter.Ipv4BindingEnabled, adapter.Ipv6BindingEnabled, adapter.DhcpEnabled, adapter.AutomaticMetric,
        adapter.InterfaceMetric, adapter.Addresses, adapter.DnsServers, adapter.Routes
    }));

    private sealed class FakeProbe(IEnumerable<string>? occupied = null) : IEthernetProbeService
    {
        private readonly HashSet<string> _occupied = new(occupied ?? [], StringComparer.Ordinal);
        public Task<string?> ProbeMacAsync(IPAddress targetAddress, IPAddress sourceAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult(_occupied.Contains(targetAddress.ToString()) ? "00-AA-BB-CC-DD-EE" : null);
        public Task<DeviceVerificationResult> VerifyAsync(IPAddress targetAddress, IPAddress sourceAddress, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceVerificationResult(targetAddress.ToString(), false, string.Empty, false));
    }

    private sealed class SuccessRunner(IReadOnlyList<AdapterInfo> appliedAdapters, IReadOnlyList<AdapterInfo>? inventoryAdapters = null) : IPowerShellRunner
    {
        private IReadOnlyList<AdapterInfo> _current = inventoryAdapters ?? appliedAdapters;
        public string ApplyScript { get; private set; } = string.Empty;
        public string RestoreScript { get; private set; } = string.Empty;
        public bool RestoreBeforeApply { get; private set; }
        public int InventoryCallCount { get; private set; }
        public Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default)
        {
            if (script.Contains("ConvertTo-Json", StringComparison.Ordinal))
            {
                InventoryCallCount++;
                return Task.FromResult(ToInventoryJson(_current));
            }
            if (script.Contains("Start-Sleep -Milliseconds 600", StringComparison.Ordinal))
            {
                RestoreBeforeApply = string.IsNullOrEmpty(ApplyScript);
                RestoreScript = script;
                if (inventoryAdapters is not null) _current = inventoryAdapters;
            }
            if (script.Contains("$target=", StringComparison.Ordinal))
            {
                ApplyScript = script;
                _current = appliedAdapters;
            }
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class FailingApplyRunner(IReadOnlyList<AdapterInfo> adapters) : IPowerShellRunner
    {
        public bool RestoreWasAttempted { get; private set; }
        public Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default)
        {
            if (script.Contains("ConvertTo-Json", StringComparison.Ordinal)) return Task.FromResult(ToInventoryJson(adapters));
            if (script.Contains("$target=", StringComparison.Ordinal)) throw new InvalidOperationException("simulated apply failure");
            if (script.Contains("Start-Sleep -Milliseconds 600", StringComparison.Ordinal)) RestoreWasAttempted = true;
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
