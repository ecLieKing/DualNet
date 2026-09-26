namespace DualNet.Core.Networking;

public static class PowerShellEscaping
{
    public static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
