using System.Text.Json;
using DualNet.Core.Models;

namespace DualNet.Core.Services;

public sealed class SnapshotStore
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public SnapshotStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualNet");
    }

    public string RootDirectory { get; }
    public string BaselinePath => Path.Combine(RootDirectory, "baseline.json");
    public string PendingPath => Path.Combine(RootDirectory, "pending.json");
    public string LogPath => Path.Combine(RootDirectory, "dualnet.log");
    public bool HasBaseline => File.Exists(BaselinePath);
    public bool HasPendingOperation => File.Exists(PendingPath);
    public string ConfirmationPath(Guid operationId) => Path.Combine(RootDirectory, $"watchdog-{operationId:N}.confirmed");
    public string ArmedPath(Guid operationId) => Path.Combine(RootDirectory, $"watchdog-{operationId:N}.armed");

    public async Task SaveBaselineIfMissingAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        if (!File.Exists(BaselinePath)) await WriteAtomicAsync(BaselinePath, snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NetworkSnapshot?> LoadBaselineAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(BaselinePath)) return null;
        await using var stream = File.OpenRead(BaselinePath);
        var snapshot = await JsonSerializer.DeserializeAsync<NetworkSnapshot>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        return snapshot with
        {
            Adapters = (snapshot.Adapters ?? []).Select(adapter => adapter with
            {
                DnsServers = (adapter.DnsServers ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
            }).ToArray()
        };
    }

    public Task MarkPendingAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        return WriteAtomicAsync(PendingPath, snapshot, cancellationToken);
    }

    public void ClearPending()
    {
        if (File.Exists(PendingPath)) File.Delete(PendingPath);
    }

    public void ClearAll()
    {
        ClearPending();
        if (File.Exists(BaselinePath)) File.Delete(BaselinePath);
        if (Directory.Exists(RootDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(RootDirectory, "watchdog-*.*")) File.Delete(path);
        }
    }

    public async Task ArmWatchdogAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        await File.WriteAllTextAsync(ArmedPath(operationId), operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfirmWatchdogAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        await File.WriteAllTextAsync(ConfirmationPath(operationId), "confirmed", cancellationToken).ConfigureAwait(false);
        if (File.Exists(ArmedPath(operationId))) File.Delete(ArmedPath(operationId));
        ClearPending();
    }

    public bool IsWatchdogConfirmed(Guid operationId) => File.Exists(ConfirmationPath(operationId));
    public bool IsWatchdogArmed(Guid operationId) => File.Exists(ArmedPath(operationId));

    public void ClearWatchdog(Guid operationId)
    {
        if (File.Exists(ArmedPath(operationId))) File.Delete(ArmedPath(operationId));
        if (File.Exists(ConfirmationPath(operationId))) File.Delete(ConfirmationPath(operationId));
    }

    public async Task LogAsync(string message)
    {
        Directory.CreateDirectory(RootDirectory);
        await File.AppendAllTextAsync(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}").ConfigureAwait(false);
    }

    private async Task WriteAtomicAsync(string path, NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temp, path, true);
    }
}
