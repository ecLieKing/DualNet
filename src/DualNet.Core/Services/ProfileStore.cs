using System.Text.Json;
using DualNet.Core.Models;

namespace DualNet.Core.Services;

public sealed class ProfileStore
{
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public ProfileStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualNet");
    }

    public string RootDirectory { get; }
    public string ProfilePath => Path.Combine(RootDirectory, "profile.json");

    public async Task<RoutingProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ProfilePath)) return new RoutingProfile();
        await using var stream = File.OpenRead(ProfilePath);
        var loaded = await JsonSerializer.DeserializeAsync<RoutingProfile>(stream, _options, cancellationToken).ConfigureAwait(false)
                     ?? new RoutingProfile();
        if (loaded.Version >= 3) return loaded;
        return loaded with
        {
            Version = 3,
            Mode = loaded.Version <= 1 ? RoutingMode.PartialTraffic : loaded.Mode,
            TargetAdapterId = loaded.TargetAdapterId ?? loaded.IndustrialAdapterId
        };
    }

    public async Task SaveAsync(RoutingProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var temporary = ProfilePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, profile, _options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, ProfilePath, true);
    }
}
