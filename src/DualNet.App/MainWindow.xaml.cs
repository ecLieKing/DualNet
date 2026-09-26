using System.Windows;
using DualNet.App.Themes;
using DualNet.App.ViewModels;

namespace DualNet.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        UpdateThemeToggle();
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(!ThemeManager.IsDark);
        UpdateThemeToggle();
    }

    private void UpdateThemeToggle() => ThemeToggle.Content = ThemeManager.IsDark ? "浅色模式" : "深色模式";
}
