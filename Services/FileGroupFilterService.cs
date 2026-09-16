using PartMap.Models;

namespace PartMap.Services;

public sealed class FileGroupFilterService
{
    public IReadOnlyList<FileGroup> Apply(
        IEnumerable<FileGroup> groups,
        ProductConfig product,
        bool showAll)
    {
        var mappedKeys = product.Hotspots
            .SelectMany(hotspot => hotspot.GroupKeys)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        var materialized = groups.ToList();
        foreach (var group in materialized)
        {
            group.IsMapped = mappedKeys.Contains(group.Key);
        }

        return materialized
            .Where(group => showAll || !group.IsMapped)
            .ToList();
    }
}
