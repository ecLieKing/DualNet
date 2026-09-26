using System.IO;
using System.Text.Json;
using System.Windows;

namespace DualNet.App.Themes;

public static class ThemeManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualNet", "ui-settings.json");

    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        var dark = false;
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (document.RootElement.TryGetProperty("darkTheme", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    dark = value.GetBoolean();
            }
        }
        catch
        {
            // 设置文件损坏时回退到浅色主题
        }
        Apply(dark, persist: false);
    }

    public static void Apply(bool dark, bool persist = true)
    {
        IsDark = dark;
        var merged = Application.Current.Resources.MergedDictionaries;
        for (var index = merged.Count - 1; index >= 0; index--)
        {
            if (merged[index].Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true)
                merged.RemoveAt(index);
        }
        var assemblyName = typeof(ThemeManager).Assembly.GetName().Name;
        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assemblyName};component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        });
        if (!persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { darkTheme = dark }));
        }
        catch
        {
            // 无法写入设置时仅本次生效
        }
    }
}
