using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace DualNet.Core.Services;

public sealed class PowerShellRunner : IPowerShellRunner
{
    public async Task<string> ExecuteAsync(string script, CancellationToken cancellationToken = default)
    {
        var wrapped = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"PowerShell 执行失败（{process.ExitCode}）。" : NormalizeError(error));
        return output.Trim();
    }

    internal static string NormalizeError(string error)
    {
        var trimmed = error.Trim();
        if (!trimmed.StartsWith("#< CLIXML", StringComparison.OrdinalIgnoreCase)) return trimmed;
        try
        {
            var xmlStart = trimmed.IndexOf("<Objs", StringComparison.Ordinal);
            if (xmlStart < 0) return trimmed;
            var document = XDocument.Parse(trimmed[xmlStart..]);
            var messages = document.Descendants()
                .Where(element => element.Name.LocalName == "S"
                                  && string.Equals((string?)element.Attribute("S"), "Error", StringComparison.OrdinalIgnoreCase))
                .Select(element => DecodeCliXmlText(element.Value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return messages.Length == 0 ? "PowerShell 执行失败。" : string.Join(Environment.NewLine, messages).Trim();
        }
        catch
        {
            return trimmed;
        }
    }

    private static string DecodeCliXmlText(string value) => value
        .Replace("_x000D__x000A_", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
        .Replace("_x000D_", "\r", StringComparison.OrdinalIgnoreCase)
        .Replace("_x000A_", "\n", StringComparison.OrdinalIgnoreCase);
}
