using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PartMap.Services;

public sealed class SliceParameterRecord
{
    public string SourceFile { get; init; } = "";
    public int? PlateId { get; init; }
    public string PlateName { get; init; } = "";
    public int Version { get; init; }
    public string TimeLabel { get; init; } = "";
    public string WeightLabel { get; init; } = "";
    public DateTime SavedAtUtc { get; init; }
    public IReadOnlyDictionary<string, string> SourceMetadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SlicedMetadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<SliceGcodeHeader> GcodeHeaders { get; init; } = [];
}

public sealed record SliceGcodeHeader(string Entry, string EstimatedTime, IReadOnlyList<double> FilamentWeightsGrams);

public sealed class SliceParameterHistoryService
{
    private const string GcodeSuffix = ".gcode.3mf";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public string Save(string source3mf, string slicedGcode3mf, string historyDirectory,
        string outputFileName, int? plateId, string plateName, int version, BambuSlicePrintSummary summary)
    {
        Directory.CreateDirectory(historyDirectory);
        var record = new SliceParameterRecord
        {
            SourceFile = Path.GetFileName(source3mf),
            PlateId = plateId,
            PlateName = plateName,
            Version = version,
            TimeLabel = summary.TimeLabel,
            WeightLabel = summary.WeightLabel,
            SavedAtUtc = DateTime.UtcNow,
            SourceMetadata = ReadMetadataFiles(source3mf),
            SlicedMetadata = ReadMetadataFiles(slicedGcode3mf),
            GcodeHeaders = ReadGcodeHeaders(slicedGcode3mf)
        };

        var stem = outputFileName.EndsWith(GcodeSuffix, StringComparison.OrdinalIgnoreCase)
            ? outputFileName[..^GcodeSuffix.Length]
            : Path.GetFileNameWithoutExtension(outputFileName);
        var target = Path.Combine(historyDirectory, stem + ".params.json");
        var temporary = Path.Combine(historyDirectory, $".partmap-params-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(temporary, target, false);
        return target;
    }
    private static IReadOnlyDictionary<string, string> ReadMetadataFiles(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.StartsWith("Metadata/", StringComparison.OrdinalIgnoreCase) &&
                     IsTextMetadata(entry.FullName)))
        {
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = reader.ReadToEnd();
        }
        return result;
    }

    private static bool IsTextMetadata(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SliceGcodeHeader> ReadGcodeHeaders(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var result = new List<SliceGcodeHeader>();
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(ReadGcodeHeader(entry));
        }
        return result;
    }
    private static SliceGcodeHeader ReadGcodeHeader(ZipArchiveEntry entry)
    {
        string estimatedTime = "";
        var weights = new List<double>();
        using var reader = new StreamReader(entry.Open());
        while (!reader.EndOfStream && (estimatedTime.Length == 0 || weights.Count == 0))
        {
            var line = reader.ReadLine() ?? "";
            if (estimatedTime.Length == 0)
            {
                var time = Regex.Match(line, @"total estimated time:\s*(?<value>[^;\r\n]+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (time.Success) estimatedTime = time.Groups["value"].Value.Trim();
            }

            if (weights.Count == 0)
            {
                var weight = Regex.Match(line,
                    @"total filament weight\s*\[g\]\s*:\s*(?<value>[0-9.,\s]+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (weight.Success)
                {
                    foreach (var value in weight.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var grams)) weights.Add(grams);
                    }
                }
            }
        }
        return new SliceGcodeHeader(entry.FullName, estimatedTime, weights);
    }
}
