using System.IO.Compression;
using System.Xml.Linq;

namespace PartMap.Services;

public sealed record ThreeMfPlateInfo(int PlateId, string Name);

public sealed class ThreeMfMetadataService
{
    public IReadOnlyList<ThreeMfPlateInfo> ReadPlates(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("Metadata/model_settings.config");
        if (entry is null) return [];

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return document.Descendants("plate")
            .Select(plate =>
            {
                var metadata = plate.Elements("metadata")
                    .Where(item => item.Attribute("key") is not null)
                    .ToDictionary(item => item.Attribute("key")!.Value,
                        item => item.Attribute("value")?.Value ?? "", StringComparer.OrdinalIgnoreCase);
                return int.TryParse(metadata.GetValueOrDefault("plater_id"), out var id)
                    ? new ThreeMfPlateInfo(id, (metadata.GetValueOrDefault("plater_name") ?? "").Trim())
                    : null;
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.PlateId)
            .ToList();
    }
}