using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using DualNet.Core.Models;
using DualNet.Core.Networking;
using DualNet.Core.Services;

namespace DualNet.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SnapshotStore _snapshots = new();
    private readonly ProfileStore _profiles = new();
    private readonly NetworkInventoryService _inventory;
    private readonly NetworkConfigurationService _configuration;
    private readonly IEthernetProbeService _probe = new EthernetProbeService();
    private readonly DispatcherTimer _confirmationTimer;
    private AdapterInfo? _selectedTarget;
    private AdapterInfo? _selectedInternetAdapter;
    private RoutingModeOption _selectedMode;
    private RuleKindOption _selectedRuleKind;
    private string _ruleInput = string.Empty;
    private string _allLocalAddress = string.Empty;
    private string _allPrefixLength = "24";
    private string _allGateway = string.Empty;
    private string _allDns = string.Empty;
    private string _statusText = "正在读取网络状态…";
    private string _targetStatus = "请选择分流网卡";
    private string _targetStatusColor = "#7B8B91";
    private string _routeStatus = "尚未应用路由";
    private string _routeStatusColor = "#7B8B91";
    private bool _isBusy;
    private bool _isApplied;
    private bool _isDirty;
    private bool _isConfirmationPending;
    private int _confirmationSeconds;
    private Guid? _confirmationOperationId;
    private Guid? _profileTargetId;
    private Guid? _profileInternetAdapterId;
    private IReadOnlyList<RoutingRule> _appliedRules = [];
    private AllTrafficSettings? _initialAllTraffic;
    private bool _initializing = true;

    public MainViewModel()
    {
        var runner = new PowerShellRunner();
        _inventory = new NetworkInventoryService(runner);
        _configuration = new NetworkConfigurationService(runner, _inventory, _snapshots, _probe);
        Modes =
        [
            new RoutingModeOption(RoutingMode.PartialTraffic, "部分流量", "只有指定 IP 或网段走分流网卡"),
            new RoutingModeOption(RoutingMode.AllTraffic, "全部流量", "严格关闭其他网卡的 IPv4 和 IPv6")
        ];
        _selectedMode = Modes[0];
        RuleKinds = [new RuleKindOption(RoutingRuleKind.Host, "单个设备 IP"), new RuleKindOption(RoutingRuleKind.Subnet, "整个网段")];
        _selectedRuleKind = RuleKinds[0];
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy && !IsConfirmationPending);
        AddRuleCommand = new AsyncCommand(AddRuleAsync, () => !IsBusy && IsPartialMode);
        ApplyCommand = new AsyncCommand(ApplyAsync, CanApply);
        RestoreCommand = new AsyncCommand(ResetNetworkAsync, CanResetNetwork);
        KeepConfigurationCommand = new AsyncCommand(KeepStrictConfigurationAsync, () => IsConfirmationPending && !IsBusy);
        RemoveRuleCommand = new RelayCommand<RoutingRuleViewModel>(RemoveRule, rule => rule is not null && !IsBusy && IsPartialMode);
        _confirmationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _confirmationTimer.Tick += OnConfirmationTick;
    }

    public ObservableCollection<AdapterInfo> Adapters { get; } = [];
    public ObservableCollection<RoutingRuleViewModel> Rules { get; } = [];
    public IReadOnlyList<RoutingModeOption> Modes { get; }
    public IReadOnlyList<RuleKindOption> RuleKinds { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand AddRuleCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand KeepConfigurationCommand { get; }
    public RelayCommand<RoutingRuleViewModel> RemoveRuleCommand { get; }

    public RoutingModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value)) return;
            OnPropertyChanged(nameof(IsPartialMode));
            OnPropertyChanged(nameof(IsAllMode));
            OnPropertyChanged(nameof(IsPartialModeSelected));
            OnPropertyChanged(nameof(IsAllModeSelected));
            OnPropertyChanged(nameof(PrimaryActionText));
            if (IsAllMode && string.IsNullOrWhiteSpace(AllLocalAddress)) PrefillAllTraffic(SelectedTarget);
            if (IsApplied) IsDirty = true;
            UpdateRouteStatus();
            RaiseCommandStates();
            if (!_initializing) _ = SaveProfileAsync();
        }
    }
    public bool IsPartialMode => SelectedMode.Mode == RoutingMode.PartialTraffic;
    public bool IsAllMode => SelectedMode.Mode == RoutingMode.AllTraffic;
    public bool IsPartialModeSelected { get => IsPartialMode; set { if (value) SelectedMode = Modes[0]; } }
    public bool IsAllModeSelected { get => IsAllMode; set { if (value) SelectedMode = Modes[1]; } }
    public bool HasRules => Rules.Count > 0;
    public string VersionText { get; } = $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.4.0"}";

    public AdapterInfo? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            var previous = _selectedTarget?.Id;
            if (!SetProperty(ref _selectedTarget, value)) return;
            PrefillAllTraffic(value);
            if (IsApplied && previous is not null && previous != value?.Id) IsDirty = true;
            if (SelectedInternetAdapter?.Id == value?.Id)
                SelectedInternetAdapter = AdapterRecommendationService.RecommendInternetAdapter(Adapters, value?.Id);
            OnPropertyChanged(nameof(InternetAdapterHint));
            UpdateTargetStatus();
            RaiseCommandStates();
            if (!_initializing) _ = SaveProfileAsync();
        }
    }

    public AdapterInfo? SelectedInternetAdapter
    {
        get => _selectedInternetAdapter;
        set
        {
            var previous = _selectedInternetAdapter?.Id;
            if (!SetProperty(ref _selectedInternetAdapter, value)) return;
            if (IsApplied && previous is not null && previous != value?.Id) IsDirty = true;
            OnPropertyChanged(nameof(InternetAdapterHint));
            RaiseCommandStates();
            if (!_initializing) _ = SaveProfileAsync();
        }
    }

    public string InternetAdapterHint => SelectedInternetAdapter is null
        ? "请选择一张具有默认路由的网卡作为其余流量出口。"
        : SelectedInternetAdapter.Id == SelectedTarget?.Id
            ? "其余流量出口必须与分流网卡不同。"
            : !SelectedInternetAdapter.HasDefaultRoute
                ? "所选网卡没有默认路由，不能作为其余流量出口。"
                : SelectedInternetAdapter.IsUp
                    ? $"未匹配的默认流量将优先走 {SelectedInternetAdapter.Name}；不检测是否真的联网。"
                    : $"{SelectedInternetAdapter.Name} 当前未连接，应用后其他默认流量可能不可用。";

    public RuleKindOption SelectedRuleKind
    {
        get => _selectedRuleKind;
        set
        {
            if (!SetProperty(ref _selectedRuleKind, value)) return;
            OnPropertyChanged(nameof(RuleInputHint));
            OnPropertyChanged(nameof(IsHostRuleKind));
            OnPropertyChanged(nameof(IsSubnetRuleKind));
            RuleInput = string.Empty;
        }
    }
    public bool IsHostRuleKind { get => SelectedRuleKind.Kind == RoutingRuleKind.Host; set { if (value) SelectedRuleKind = RuleKinds[0]; } }
    public bool IsSubnetRuleKind { get => SelectedRuleKind.Kind == RoutingRuleKind.Subnet; set { if (value) SelectedRuleKind = RuleKinds[1]; } }
    public string RuleInput { get => _ruleInput; set => SetProperty(ref _ruleInput, value); }
    public string RuleInputHint => SelectedRuleKind.Kind == RoutingRuleKind.Host ? "例如：192.168.10.6" : "例如：192.168.10 或 192.168.10.0/24";
    public string AllLocalAddress { get => _allLocalAddress; set { if (SetProperty(ref _allLocalAddress, value) && IsApplied) IsDirty = true; } }
    public string AllPrefixLength { get => _allPrefixLength; set { if (SetProperty(ref _allPrefixLength, value) && IsApplied) IsDirty = true; } }
    public string AllGateway { get => _allGateway; set { if (SetProperty(ref _allGateway, value) && IsApplied) IsDirty = true; } }
    public string AllDns { get => _allDns; set { if (SetProperty(ref _allDns, value) && IsApplied) IsDirty = true; } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string TargetStatus { get => _targetStatus; private set => SetProperty(ref _targetStatus, value); }
    public string TargetStatusColor { get => _targetStatusColor; private set => SetProperty(ref _targetStatusColor, value); }
    public string RouteStatus { get => _routeStatus; private set => SetProperty(ref _routeStatus, value); }
    public string RouteStatusColor { get => _routeStatusColor; private set => SetProperty(ref _routeStatusColor, value); }
    public bool HasBaseline => _snapshots.HasBaseline;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(PrimaryActionText)); RaiseCommandStates(); } } }
    public bool IsApplied { get => _isApplied; private set { if (SetProperty(ref _isApplied, value)) OnPropertyChanged(nameof(PrimaryActionText)); } }
    public bool IsDirty { get => _isDirty; private set { if (SetProperty(ref _isDirty, value)) UpdateRouteStatus(); } }
    public bool IsConfirmationPending { get => _isConfirmationPending; private set { if (SetProperty(ref _isConfirmationPending, value)) RaiseCommandStates(); } }
    public int ConfirmationSeconds { get => _confirmationSeconds; private set { if (SetProperty(ref _confirmationSeconds, value)) OnPropertyChanged(nameof(ConfirmationText)); } }
    public string ConfirmationText => $"严格模式已临时生效，请在 {ConfirmationSeconds} 秒内确认；否则自动恢复所有网卡。";
    public string PrimaryActionText => IsBusy ? "正在更新…" : IsApplied ? "更新路由" : IsAllMode ? "应用全部流量" : "应用部分流量";

    public async Task InitializeAsync()
    {
        var profile = await _profiles.LoadAsync();
        _profileTargetId = profile.TargetAdapterId ?? profile.IndustrialAdapterId;
        _profileInternetAdapterId = profile.InternetAdapterId;
        _appliedRules = profile.AppliedRules.Count > 0 ? profile.AppliedRules : profile.Rules;
        _initialAllTraffic = string.IsNullOrWhiteSpace(profile.AllTraffic.LocalAddress)
                             && string.IsNullOrWhiteSpace(profile.AllTraffic.Gateway)
            ? null
            : profile.AllTraffic;
        SelectedMode = Modes.Single(mode => mode.Mode == profile.Mode);
        foreach (var rule in profile.Rules) AddRuleViewModel(new RoutingRuleViewModel(rule));
        UpdateCoverage();
        IsApplied = HasBaseline;
        await RefreshAsync();
        if (_snapshots.HasPendingOperation)
        {
            var answer = MessageBox.Show("检测到未完成的网络配置。为保证安全，是否立即恢复原网络？", "双网通", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) await RestoreBaselineAfterInterruptionAsync();
        }
        else if (IsApplied)
        {
            var baseline = await _snapshots.LoadBaselineAsync();
            RouteStatus = baseline?.Version < 3 ? "检测到旧版配置，请先清除分流并启用 DHCP" : "路由配置已生效";
            RouteStatusColor = baseline?.Version < 3 ? "#D49A18" : "#15956F";
            if (IsPartialMode) await VerifyPartialRulesAsync();
        }
        _initializing = false;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "正在刷新网卡和路由状态…";
        try
        {
            var targetId = SelectedTarget?.Id ?? _profileTargetId;
            var internetAdapterId = SelectedInternetAdapter?.Id ?? _profileInternetAdapterId;
            var adapters = await _inventory.GetAdaptersAsync();
            Adapters.Clear();
            foreach (var adapter in adapters) Adapters.Add(adapter);
            SelectedTarget = Adapters.FirstOrDefault(adapter => adapter.Id == targetId) ?? AdapterRecommendationService.RecommendTarget(Adapters);
            SelectedInternetAdapter = Adapters.FirstOrDefault(adapter => adapter.Id == internetAdapterId && adapter.Id != SelectedTarget?.Id)
                                      ?? AdapterRecommendationService.RecommendInternetAdapter(Adapters, SelectedTarget?.Id);
            OnPropertyChanged(nameof(InternetAdapterHint));
            if (_initialAllTraffic is not null)
            {
                ApplyAllTrafficFields(_initialAllTraffic);
                _initialAllTraffic = null;
            }
            UpdateTargetStatus();
            UpdateRouteStatus();
            StatusText = $"发现 {Adapters.Count} 张可见网卡；不检测互联网，也不要求网卡存在外网。";
        }
        catch (Exception exception)
        {
            StatusText = "读取网络状态失败。";
            MessageBox.Show(exception.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task AddRuleAsync()
    {
        if (!RoutingRuleParser.TryParse(SelectedRuleKind.Kind, RuleInput, out var rule, out var error))
        {
            MessageBox.Show(error, "地址格式不正确", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (Rules.Any(existing => existing.DestinationPrefix == rule!.DestinationPrefix))
        {
            MessageBox.Show("这条目标已经添加。", "重复规则", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var shared = Rules.Select(item => item.ToModel()).FirstOrDefault(item => item.SourceNetworkPrefix == rule!.SourceNetworkPrefix)?.LocalAddress;
        if (shared is not null) rule = rule! with { LocalAddress = shared };
        var viewModel = new RoutingRuleViewModel(rule!);
        AddRuleViewModel(viewModel);
        if (IsApplied) IsDirty = true;
        RuleInput = string.Empty;
        UpdateCoverage();
        await ProbeBeforeApplyAsync(viewModel);
        await SaveProfileAsync();
        RaiseCommandStates();
    }

    private async Task ProbeBeforeApplyAsync(RoutingRuleViewModel rule)
    {
        if (rule.Kind != RoutingRuleKind.Host || SelectedTarget is null) return;
        var target = IPAddress.Parse(rule.DestinationPrefix[..rule.DestinationPrefix.IndexOf('/')]);
        var mac = await _probe.ProbeMacAsync(target, SelectedTarget.ProbeSourceAddress ?? IPAddress.Any);
        rule.StatusText = mac is null ? "暂未在分流网卡发现设备" : $"已在分流网卡发现 {target}";
        rule.StatusDetail = mac is null ? "应用路由后会再次验证" : $"MAC {mac}";
        rule.StatusColor = mac is null ? "#D49A18" : "#15956F";
    }

    private void RemoveRule(RoutingRuleViewModel? rule)
    {
        if (rule is null) return;
        Rules.Remove(rule);
        rule.PropertyChanged -= OnRulePropertyChanged;
        OnPropertyChanged(nameof(HasRules));
        if (IsApplied) IsDirty = true;
        UpdateCoverage();
        _ = SaveProfileAsync();
        RaiseCommandStates();
    }

    private async Task ApplyAsync()
    {
        if (SelectedTarget is null)
        {
            MessageBox.Show("请选择分流网卡。", "缺少网卡", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!SelectedTarget.IsUp && MessageBox.Show("所选网卡当前未连接，应用后可能立即失联。是否继续？", "网卡未连接", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (IsPartialMode && SelectedInternetAdapter is null)
        {
            MessageBox.Show("请选择一张具有默认路由的其余流量出口。", "缺少其余流量出口", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsPartialMode && SelectedInternetAdapter?.Id == SelectedTarget.Id)
        {
            MessageBox.Show("分流网卡和其余流量出口不能相同。", "网卡选择冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsPartialMode && SelectedInternetAdapter?.HasDefaultRoute == false)
        {
            MessageBox.Show("所选其余流量出口没有默认路由。", "出口不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (IsPartialMode && SelectedInternetAdapter?.IsUp == false
            && MessageBox.Show("所选其余流量出口当前未连接，应用后其他默认流量可能不可用。是否继续？", "出口网卡未连接", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (IsAllMode && MessageBox.Show("严格模式会临时关闭所有其他可见网卡的 IPv4 和 IPv6。应用后必须在 60 秒内确认保留，否则自动恢复。是否继续？", "严格模式确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var configuration = BuildConfiguration();
        var freezesDhcp = IsPartialMode && SelectedTarget.DhcpEnabled
                          && configuration.Rules.Any(rule => SelectedTarget.Addresses.All(address => address.Address != rule.LocalAddress));
        var routeApplied = false;
        IsBusy = true;
        StatusText = "正在保存全部网卡基线并应用路由…";
        try
        {
            var progress = new Progress<string>(message =>
            {
                StatusText = message;
                RouteStatus = "正在更新路由";
                RouteStatusColor = "#D49A18";
            });
            var result = await _configuration.ApplyAsync(configuration, Adapters.ToArray(), StartWatchdogAsync, progress);
            routeApplied = true;
            ApplyResolvedConfiguration(result.Configuration);
            _appliedRules = result.Configuration.Rules.ToArray();
            IsApplied = true;
            IsDirty = false;
            await SaveProfileAsync();
            if (result.ConfirmationOperationId is Guid operationId)
            {
                _confirmationOperationId = operationId;
                ConfirmationSeconds = 60;
                IsConfirmationPending = true;
                _confirmationTimer.Start();
                RouteStatus = "严格路由临时生效，等待确认";
                RouteStatusColor = "#D49A18";
                StatusText = "未检测互联网；确认前由独立监护进程保护恢复。";
            }
            else
            {
                RouteStatus = $"部分流量已通过 {SelectedTarget.Name} 路由";
                if (result.Configuration.InternetAdapterId is not null)
                    RouteStatus += $" · 其余流量出口 {SelectedInternetAdapter?.Name}";
                RouteStatusColor = "#15956F";
                StatusText = "路由已生效，正在后台确认设备状态…";
                await VerifyPartialRulesAsync();
                StatusText = freezesDhcp
                    ? "路由已应用；分流网卡当前 DHCP 参数已冻结，清除分流时会重新启用 DHCP。"
                    : "部分流量路由已应用；其他流量保持系统原状。";
            }
        }
        catch (Exception exception)
        {
            try { await _snapshots.LogAsync($"界面应用流程失败：{exception}"); } catch { }
            if (routeApplied)
            {
                StatusText = "路由已应用，但保存或状态验证失败；可刷新状态或清除分流。";
                MessageBox.Show($"路由已经应用，但后续处理失败：\n{exception.Message}\n\n可点击“刷新网络”或“清除分流并启用 DHCP”。", "双网通", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                StatusText = "应用失败，核心事务已尝试恢复原网络。";
                MessageBox.Show($"应用路由失败：\n{exception.Message}", "双网通", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasBaseline)); RaiseCommandStates(); }
    }

    private RouteConfiguration BuildConfiguration()
    {
        if (!int.TryParse(AllPrefixLength, out var prefix)) prefix = 0;
        var dns = AllDns.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new RouteConfiguration
        {
            TargetAdapterId = SelectedTarget!.Id,
            InternetAdapterId = IsPartialMode ? SelectedInternetAdapter?.Id : null,
            Mode = SelectedMode.Mode,
            AllTraffic = new AllTrafficSettings { LocalAddress = AllLocalAddress.Trim(), PrefixLength = prefix, Gateway = AllGateway.Trim(), DnsServers = dns },
            Rules = Rules.Select(rule => rule.ToModel()).ToArray()
        };
    }

    private void ApplyResolvedConfiguration(RouteConfiguration configuration)
    {
        ApplyAllTrafficFields(configuration.AllTraffic);
        foreach (var model in configuration.Rules)
        {
            var item = Rules.SingleOrDefault(rule => rule.Id == model.Id);
            item?.ApplyResolvedModel(model);
        }
    }

    private async Task VerifyPartialRulesAsync()
    {
        foreach (var rule in Rules)
        {
            if (rule.Kind == RoutingRuleKind.Subnet)
            {
                rule.StatusText = $"{rule.Target} 已通过分流网卡路由";
                rule.StatusDetail = $"本机地址 {rule.LocalAddress}";
                rule.StatusColor = "#15956F";
                continue;
            }
            try
            {
                var target = IPAddress.Parse(rule.DestinationPrefix[..rule.DestinationPrefix.IndexOf('/')]);
                var verification = await _probe.VerifyAsync(target, IPAddress.Parse(rule.LocalAddress));
                rule.StatusText = verification.ArpReachable ? $"已通过分流网卡找到 {target}" : $"未在分流网卡找到 {target}";
                rule.StatusDetail = verification.ArpReachable ? $"MAC {verification.MacAddress}" + (verification.PingReachable ? " · Ping 正常" : " · 不响应 Ping") : "请检查设备、线路和地址";
                rule.StatusColor = verification.ArpReachable ? "#15956F" : "#C4564A";
            }
            catch (Exception exception)
            {
                rule.StatusText = "路由已应用，设备状态验证失败";
                rule.StatusDetail = exception.Message;
                rule.StatusColor = "#D49A18";
                try { await _snapshots.LogAsync($"目标 {rule.DestinationPrefix} 状态验证失败：{exception}"); } catch { }
            }
        }
    }

    private Task StartWatchdogAsync(Guid operationId)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序，不能启动恢复监护进程。");
        Process.Start(new ProcessStartInfo(executable, $"--watchdog {operationId:N}") { UseShellExecute = false, CreateNoWindow = true });
        return Task.CompletedTask;
    }

    private async Task KeepStrictConfigurationAsync()
    {
        if (_confirmationOperationId is not Guid operationId) return;
        IsBusy = true;
        try
        {
            await _configuration.ConfirmStrictConfigurationAsync(operationId);
            _confirmationTimer.Stop();
            IsConfirmationPending = false;
            _confirmationOperationId = null;
            RouteStatus = $"全部 IP 流量仅允许使用 {SelectedTarget?.Name}";
            RouteStatusColor = "#15956F";
            StatusText = "严格配置已保留；未检测所选网卡是否能够上网。";
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasBaseline)); }
    }

    private async Task ResetNetworkAsync()
    {
        if (SelectedTarget is null) return;
        var scope = IsPartialMode && SelectedInternetAdapter is not null
            ? $"“{SelectedTarget.Name}”和“{SelectedInternetAdapter.Name}”"
            : $"“{SelectedTarget.Name}”";
        if (MessageBox.Show($"清除分流规则和相关路由，将{scope}切换为 DHCP。分流前已有的静态 IPv4 地址及手动 DNS 会保留。是否继续？", "清除分流并启用 DHCP", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            _confirmationTimer.Stop();
            await _configuration.ResetRoutingAsync(SelectedTarget.Id,
                IsPartialMode ? SelectedInternetAdapter?.Id : null, SelectedMode.Mode,
                _appliedRules);
            IsConfirmationPending = false;
            _confirmationOperationId = null;
            IsApplied = false;
            IsDirty = false;
            foreach (var rule in Rules) rule.PropertyChanged -= OnRulePropertyChanged;
            Rules.Clear();
            _appliedRules = [];
            OnPropertyChanged(nameof(HasRules));
            UpdateCoverage();
            await SaveProfileAsync();
            RouteStatus = "分流规则已清除，所选网卡已切换为 DHCP";
            RouteStatusColor = "#15956F";
            StatusText = "已移除分流配置产生的路由和辅助地址。";
            await RefreshAsync();
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "网络重置失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsBusy = false; OnPropertyChanged(nameof(HasBaseline)); RaiseCommandStates(); }
    }

    private async Task RestoreBaselineAfterInterruptionAsync()
    {
        try
        {
            await _configuration.RestoreBaselineAsync();
            _appliedRules = [];
            IsConfirmationPending = false;
            _confirmationOperationId = null;
            IsApplied = false;
            IsDirty = false;
            RouteStatus = "已恢复原始网络基线";
            RouteStatusColor = "#15956F";
            await RefreshAsync();
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void OnConfirmationTick(object? sender, EventArgs e)
    {
        if (ConfirmationSeconds > 0) ConfirmationSeconds--;
        if (ConfirmationSeconds > 0) return;
        _confirmationTimer.Stop();
        IsConfirmationPending = false;
        RouteStatus = "确认超时，监护进程正在恢复原网络";
        RouteStatusColor = "#D49A18";
        StatusText = "请稍候，然后点击刷新网络确认恢复结果。";
        await Task.Delay(2500);
        IsApplied = _snapshots.HasBaseline;
        IsDirty = false;
        await RefreshAsync();
    }

    private void PrefillAllTraffic(AdapterInfo? adapter)
    {
        if (adapter is null) return;
        var address = adapter.Addresses.FirstOrDefault(item => !item.Address.StartsWith("169.254.", StringComparison.Ordinal));
        AllLocalAddress = address?.Address ?? string.Empty;
        AllPrefixLength = (address?.PrefixLength ?? 24).ToString();
        AllGateway = adapter.DefaultRoute?.NextHop ?? string.Empty;
        AllDns = string.Join(", ", adapter.DnsServers);
    }

    private void ApplyAllTrafficFields(AllTrafficSettings settings)
    {
        AllLocalAddress = settings.LocalAddress;
        AllPrefixLength = settings.PrefixLength.ToString();
        AllGateway = settings.Gateway;
        AllDns = string.Join(", ", settings.DnsServers);
    }

    private void UpdateTargetStatus()
    {
        if (SelectedTarget is null)
        {
            TargetStatus = "请选择分流网卡";
            TargetStatusColor = "#7B8B91";
        }
        else if (SelectedTarget.IsUp)
        {
            TargetStatus = $"分流网卡已连接 · {SelectedTarget.Name}";
            TargetStatusColor = "#15956F";
        }
        else
        {
            TargetStatus = "分流网卡未连接 · 仍允许配置";
            TargetStatusColor = "#D49A18";
        }
    }

    private void UpdateRouteStatus()
    {
        if (IsConfirmationPending) return;
        if (IsApplied && IsDirty)
        {
            RouteStatus = "配置已更改，请点击更新路由";
            RouteStatusColor = "#D49A18";
        }
        else if (IsApplied)
        {
            RouteStatus = IsAllMode ? "全部流量路由已应用" : $"部分流量路由已应用 · {Rules.Count} 条";
            if (IsPartialMode && SelectedInternetAdapter is not null)
                RouteStatus += $" · 其余流量 {SelectedInternetAdapter.Name}";
            RouteStatusColor = "#15956F";
        }
        else
        {
            RouteStatus = IsAllMode ? "全部流量模式 · 不检测互联网" : $"部分流量模式 · {Rules.Count} 条规则";
            RouteStatusColor = "#2D7FB8";
        }
    }

    private bool CanApply() => !IsBusy && !IsConfirmationPending && SelectedTarget is not null
        && (IsAllMode || Rules.Count > 0 && SelectedInternetAdapter is not null
            && SelectedInternetAdapter.Id != SelectedTarget.Id && SelectedInternetAdapter.HasDefaultRoute);
    private bool CanResetNetwork() => !IsBusy && SelectedTarget is not null;
    private void UpdateCoverage()
    {
        var models = Rules.Select(item => item.ToModel()).ToArray();
        foreach (var rule in Rules) rule.IsCovered = RoutingRuleParser.IsCoveredBySubnet(rule.ToModel(), models);
    }

    private async Task SaveProfileAsync()
    {
        var all = BuildConfigurationOrDefaultAll();
        await _profiles.SaveAsync(new RoutingProfile
        {
            Version = 3,
            Mode = SelectedMode.Mode,
            TargetAdapterId = SelectedTarget?.Id,
            InternetAdapterId = IsPartialMode ? SelectedInternetAdapter?.Id : null,
            AllTraffic = all,
            Rules = Rules.Select(item => item.ToModel()).ToArray(),
            AppliedRules = _appliedRules
        });
    }

    private AllTrafficSettings BuildConfigurationOrDefaultAll()
    {
        _ = int.TryParse(AllPrefixLength, out var prefix);
        var dns = AllDns.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new AllTrafficSettings { LocalAddress = AllLocalAddress.Trim(), PrefixLength = prefix == 0 ? 24 : prefix, Gateway = AllGateway.Trim(), DnsServers = dns };
    }

    private void AddRuleViewModel(RoutingRuleViewModel rule)
    {
        rule.PropertyChanged += OnRulePropertyChanged;
        Rules.Add(rule);
        OnPropertyChanged(nameof(HasRules));
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RoutingRuleViewModel.LocalAddress) && IsApplied) IsDirty = true;
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        AddRuleCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        KeepConfigurationCommand.RaiseCanExecuteChanged();
        RemoveRuleCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasBaseline));
    }
}

public sealed record RoutingModeOption(RoutingMode Mode, string Label, string Description);
