using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

public sealed class RoutingRuleViewModel : ObservableObject
{
    private RoutingRule _model;
    private string _statusText = "等待启用分流";
    private string _statusDetail = string.Empty;
    private string _statusColor = "#7B8B91";
    private bool _isCovered;

    public RoutingRuleViewModel(RoutingRule model) => _model = model;

    public Guid Id => _model.Id;
    public RoutingRuleKind Kind => _model.Kind;
    public string KindLabel => Kind == RoutingRuleKind.Host ? "单个设备" : "整个网段";
    public string Target => _model.DisplayTarget;
    public string DestinationPrefix => _model.DestinationPrefix;
    public string LocalAddress
    {
        get => _model.LocalAddress;
        set
        {
            if (value == _model.LocalAddress) return;
            _model = _model with { LocalAddress = value.Trim(), IsLocalAddressManual = true };
            OnPropertyChanged();
        }
    }
    public bool IsLocalAddressManual => _model.IsLocalAddressManual;
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string StatusDetail { get => _statusDetail; set => SetProperty(ref _statusDetail, value); }
    public string StatusColor { get => _statusColor; set => SetProperty(ref _statusColor, value); }
    public bool IsCovered { get => _isCovered; set { if (SetProperty(ref _isCovered, value)) OnPropertyChanged(nameof(CoveredText)); } }
    public string CoveredText => IsCovered ? "已包含在网段规则中，仍单独验证设备状态" : string.Empty;

    public RoutingRule ToModel() => _model;

    public void ApplyResolvedModel(RoutingRule model)
    {
        _model = model;
        OnPropertyChanged(nameof(LocalAddress));
        OnPropertyChanged(nameof(IsLocalAddressManual));
    }
}

public sealed record RuleKindOption(RoutingRuleKind Kind, string Label);
