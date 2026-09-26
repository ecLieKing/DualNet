using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Reflection;
using DualNet.App;
using DualNet.App.Themes;
using DualNet.App.ViewModels;
using DualNet.Core.Models;
using DualNet.Core.Networking;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeManager.Apply(dark: false, persist: false);
        var viewModel = CreateViewModel();
        var light = CreateSurface();

        Render(app, light.root, viewModel, 1040, 780, "ui-preview.png");

        viewModel.SelectedMode = viewModel.Modes.Single(mode => mode.Mode == RoutingMode.AllTraffic);
        viewModel.AllLocalAddress = "10.64.20.9";
        viewModel.AllPrefixLength = "22";
        viewModel.AllGateway = "10.64.20.1";
        viewModel.AllDns = "223.5.5.5, 119.29.29.29";
        Render(app, light.root, viewModel, 1040, 780, "ui-preview-all.png");

        light.window.Close();
        ThemeManager.Apply(dark: true, persist: false);
        var dark = CreateSurface();
        Render(app, dark.root, viewModel, 1040, 780, "ui-preview-all-dark.png");

        SetPrivate(viewModel, nameof(MainViewModel.IsConfirmationPending), true);
        SetPrivate(viewModel, nameof(MainViewModel.ConfirmationSeconds), 47);
        Render(app, dark.root, viewModel, 1040, 780, "ui-preview-confirm-dark.png");

        dark.window.Close();
        ThemeManager.Apply(dark: false, persist: false);
        SetPrivate(viewModel, nameof(MainViewModel.IsConfirmationPending), false);
        viewModel.SelectedMode = viewModel.Modes.Single(mode => mode.Mode == RoutingMode.PartialTraffic);
        var light2 = CreateSurface();
        Render(app, light2.root, viewModel, 900, 720, "ui-preview-narrow.png");

        viewModel.Rules.Clear();
        var viewModelType = typeof(MainViewModel);
        viewModelType.GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, new object[] { nameof(MainViewModel.HasRules) });
        viewModelType.GetMethod("UpdateRouteStatus", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, null);
        viewModelType.GetMethod("RaiseCommandStates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, null);
        Render(app, light2.root, viewModel, 1040, 780, "ui-preview-empty.png");

        VerifyComboBoxToggle(light2.root);

        light2.window.Close();
        Console.WriteLine("UI_BINDING_INITIALIZATION=OK");
        return 0;
    }

    private static (Window window, FrameworkElement root) CreateSurface()
    {
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        return (window, root);
    }

    private static MainViewModel CreateViewModel()
    {
        var surface = CreateSurface();
        var viewModel = (MainViewModel)surface.window.DataContext;
        surface.window.Close();
        var targetAdapter = new AdapterInfo
        {
            Id = Guid.NewGuid(), InterfaceIndex = 15, Name = "WLAN", Description = "Wi-Fi 设备网卡", Status = "Up",
            PhysicalMediaType = "NdisPhysicalMediumNative802_11", IsPhysical = true, DhcpEnabled = true,
            AutomaticMetric = true, InterfaceMetric = 50,
            Addresses = [new IpAddressInfo("192.168.40.36", 24, "Dhcp", "Dhcp", false)],
            DnsServers = ["192.168.40.1"], Routes = [new RouteInfo("0.0.0.0/0", "192.168.40.1", 0, "NetMgmt", false)]
        };
        var internetAdapter = new AdapterInfo
        {
            Id = Guid.NewGuid(), InterfaceIndex = 50, Name = "以太网 2", Description = "USB 网络共享", Status = "Up",
            PhysicalMediaType = "NdisPhysicalMedium802_3", IsPhysical = true, DhcpEnabled = true,
            AutomaticMetric = true, InterfaceMetric = 25,
            Addresses = [new IpAddressInfo("10.64.10.203", 24, "Dhcp", "Dhcp", false)],
            DnsServers = ["10.64.10.178"], Routes = [new RouteInfo("0.0.0.0/0", "10.64.10.178", 0, "NetMgmt", false)]
        };
        viewModel.Adapters.Add(targetAdapter);
        viewModel.Adapters.Add(internetAdapter);
        viewModel.SelectedTarget = targetAdapter;
        viewModel.UseInternetAdapter = true;
        viewModel.SelectedInternetAdapter = internetAdapter;
        RoutingRuleParser.TryParse(RoutingRuleKind.Host, "192.168.10.6", out var host, out _);
        RoutingRuleParser.TryParse(RoutingRuleKind.Subnet, "10.20.0.0/16", out var subnet, out _);
        viewModel.Rules.Add(new RoutingRuleViewModel(host!)
        {
            StatusText = "已在目标网卡发现 192.168.10.6",
            StatusDetail = "MAC 02-11-22-33-44-55 · 应用路由后会再次验证",
            StatusColor = "#15956F"
        });
        viewModel.Rules.Add(new RoutingRuleViewModel(subnet!)
        {
            StatusText = "等待应用路由",
            StatusDetail = string.Empty,
            StatusColor = "#7B8B91"
        });
        return viewModel;
    }

    private static void SetPrivate(object target, string propertyName, object value)
        => target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(target, value);

    private static void VerifyComboBoxToggle(FrameworkElement root)
    {
        // ComboBox 会强制未加载(IsLoaded=false)控件的下拉保持关闭,因此须把内容放进真实窗口显示后再验证
        var host = new Window
        {
            Content = root, Width = 1040, Height = 780,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false
        };
        host.Show();
        host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        try
        {
            var combos = FindVisualChildren<ComboBox>(root).ToList();
            if (combos.Count == 0) throw new InvalidOperationException("未找到 ComboBox。");
            var onToggle = typeof(ToggleButton).GetMethod("OnToggle", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var combo in combos)
            {
                if (combo.Template.FindName("DropDownToggle", combo) is not ToggleButton toggle)
                    throw new InvalidOperationException("ComboBox 模板缺少 DropDownToggle。");
                onToggle.Invoke(toggle, null);
                if (toggle.IsChecked != true) throw new InvalidOperationException("ToggleButton 未切换 IsChecked。");
                if (!combo.IsDropDownOpen) throw new InvalidOperationException("点击未展开下拉框。");
                onToggle.Invoke(toggle, null);
                if (combo.IsDropDownOpen) throw new InvalidOperationException("再次点击未收起下拉框。");
            }
            Console.WriteLine($"COMBOBOX_TOGGLE=OK ({combos.Count})");
        }
        finally
        {
            host.Close();
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private static void Render(App app, FrameworkElement root, MainViewModel viewModel, int width, int height, string fileName)
    {
        root.DataContext = viewModel;
        if (root is System.Windows.Controls.Grid grid) grid.Background = (Brush)app.FindResource("PageBrush");
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Environment.CurrentDirectory, "work", fileName));
        encoder.Save(stream);
    }
}
