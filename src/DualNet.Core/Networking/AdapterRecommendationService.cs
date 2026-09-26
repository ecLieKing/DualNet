using DualNet.Core.Models;

namespace DualNet.Core.Networking;

public static class AdapterRecommendationService
{
    public static AdapterInfo? RecommendTarget(IEnumerable<AdapterInfo> adapters) => adapters
        .OrderByDescending(a => a.IsUp)
        .ThenByDescending(a => a.HasDefaultRoute)
        .ThenByDescending(a => a.IsPhysical)
        .ThenBy(a => a.InterfaceMetric)
        .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        .FirstOrDefault();

    public static AdapterInfo? RecommendInternetAdapter(IEnumerable<AdapterInfo> adapters, Guid? targetAdapterId) => adapters
        .Where(adapter => adapter.Id != targetAdapterId)
        .OrderByDescending(adapter => adapter.IsUp && adapter.HasDefaultRoute)
        .ThenByDescending(adapter => adapter.HasDefaultRoute)
        .ThenByDescending(adapter => adapter.IsUp)
        .ThenByDescending(adapter => adapter.IsPhysical)
        .ThenBy(adapter => adapter.InterfaceMetric)
        .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
        .FirstOrDefault();

}
