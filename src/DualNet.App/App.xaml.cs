using System.Windows;
using DualNet.Core.Services;

namespace DualNet.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show($"发生未处理错误：\n{e.Exception.Message}\n\n详细日志位于 %LocalAppData%\\DualNet。", "双网通", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0].Equals("--watchdog", StringComparison.OrdinalIgnoreCase)
                               && Guid.TryParseExact(e.Args[1], "N", out var operationId))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunWatchdogAsync(operationId);
            return;
        }

        Themes.ThemeManager.Initialize();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private async Task RunWatchdogAsync(Guid operationId)
    {
        var store = new SnapshotStore();
        try
        {
            var runner = new PowerShellRunner();
            var inventory = new NetworkInventoryService(runner);
            var configuration = new NetworkConfigurationService(runner, inventory, store);
            var restored = await WatchdogCoordinator.WaitAndRecoverAsync(
                store, operationId, TimeSpan.FromSeconds(60), () => configuration.RestoreBaselineAsync());
            if (restored) await store.LogAsync($"严格模式操作 {operationId:N} 未确认，监护进程已自动恢复。");
        }
        catch (Exception exception)
        {
            await store.LogAsync($"严格模式监护恢复失败：{exception.Message}");
        }
        finally
        {
            Dispatcher.Invoke(Shutdown);
        }
    }
}
