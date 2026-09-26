using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using DualNet.Core.Models;

namespace DualNet.Core.Services;

public interface IEthernetProbeService
{
    Task<string?> ProbeMacAsync(IPAddress targetAddress, IPAddress sourceAddress, CancellationToken cancellationToken = default);
    Task<DeviceVerificationResult> VerifyAsync(IPAddress targetAddress, IPAddress sourceAddress, CancellationToken cancellationToken = default);
}

public sealed class EthernetProbeService : IEthernetProbeService
{
    public Task<string?> ProbeMacAsync(IPAddress targetAddress, IPAddress sourceAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new byte[6];
        var length = buffer.Length;
        var destination = BitConverter.ToInt32(targetAddress.GetAddressBytes(), 0);
        var source = sourceAddress.Equals(IPAddress.Any) ? 0 : BitConverter.ToInt32(sourceAddress.GetAddressBytes(), 0);
        var result = SendARP(destination, source, buffer, ref length);
        var mac = result == 0 && length > 0
            ? string.Join("-", buffer.Take(length).Select(value => value.ToString("X2")))
            : null;
        return Task.FromResult(mac);
    }

    public async Task<DeviceVerificationResult> VerifyAsync(IPAddress targetAddress, IPAddress sourceAddress, CancellationToken cancellationToken = default)
    {
        var mac = await ProbeMacAsync(targetAddress, sourceAddress, cancellationToken).ConfigureAwait(false);
        var ping = await PingFromSourceAsync(targetAddress, sourceAddress, cancellationToken).ConfigureAwait(false);
        return new DeviceVerificationResult(targetAddress.ToString(), mac is not null, mac ?? string.Empty, ping);
    }

    private static async Task<bool> PingFromSourceAsync(IPAddress target, IPAddress source, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "ping.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-w");
        start.ArgumentList.Add("600");
        if (!source.Equals(IPAddress.Any))
        {
            start.ArgumentList.Add("-S");
            start.ArgumentList.Add(source.ToString());
        }
        start.ArgumentList.Add(target.ToString());

        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destinationIp, int sourceIp, byte[] macAddress, ref int physicalAddressLength);
}
