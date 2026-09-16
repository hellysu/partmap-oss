using System.Text.RegularExpressions;
using PartMap.Models;

namespace PartMap.Services;

public sealed partial class FileGroupingService
{
    public const string HistoryDirectoryName = "历史";
    public const string GcodeDirectoryName = "gcode";
    private static readonly string[] PrinterPrefixes =
    [
        "H2D", "P1S", "P1P", "X1C", "X1E", "A1MINI", "A1"
    ];

    public string DeriveGroupKey(string fileName, ProductConfig product)
    {
        var displayName = GetDisplayName(fileName);
        var value = displayName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(displayName).Trim()
            : displayName.Trim();

        foreach (var prefix in PrinterPrefixes)
        {
            value = Regex.Replace(value, $"^{Regex.Escape(prefix)}[-_ ]*", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (!string.IsNullOrWhiteSpace(product.Name))
        {
            value = Regex.Replace(value, $"^{Regex.Escape(product.Name)}[-_ ]*", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        foreach (var rule in product.KeywordRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                continue;
            }

            value = rule.IsRegex
                ? Regex.Replace(value, rule.Pattern, rule.Replacement, RegexOptions.IgnoreCase)
                : value.Replace(rule.Pattern, rule.Replacement, StringComparison.OrdinalIgnoreCase);
        }

        value = GeneratedGcodeMetadataSuffixRegex().Replace(value, string.Empty);
        value = QuantitySuffixRegex().Replace(value, string.Empty);
        value = TrailingVersionRegex().Replace(value, string.Empty);
        value = SeparatorRegex().Replace(value, "-").Trim('-', '_', ' ');

        return string.IsNullOrWhiteSpace(value) ? "未分类" : value;
    }

    public IReadOnlyList<ModelFile> Scan(ProductConfig product, string modelRoot)
    {
        modelRoot = Path.GetFullPath(modelRoot);
        if (!Directory.Exists(modelRoot))
        {
            throw new DirectoryNotFoundException($"模型目录不存在：{modelRoot}");
        }

        var previousBindings = product.FileBindings
            .GroupBy(binding => NormalizeRelative(binding.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var excludedDirectories = (product.ExcludedDirectories ?? [])
            .Select(NormalizeExcludedDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = EnumerateModelFiles(modelRoot, product.IncludeSubdirectories, excludedDirectories)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .Select(path =>
            {
                var relative = NormalizeRelative(Path.GetRelativePath(modelRoot, path));
                var group = previousBindings.TryGetValue(relative, out var binding)
                    ? binding.GroupKey
                    : DeriveGroupKey(Path.GetFileName(path), product);

                return new ModelFile
                {
                    FullPath = path,
                    RelativePath = relative,
                    DisplayName = GetDisplayName(path),
                    GroupKey = group,
                    LastModifiedLocal = File.GetLastWriteTime(path)
                };
            })
            .ToList();

        var visibleBindings = files.Select(file =>
        {
            var old = previousBindings.GetValueOrDefault(file.RelativePath);
            return new FileBinding
            {
                RelativePath = file.RelativePath,
                GroupKey = file.GroupKey,
                ManualOverride = old?.ManualOverride ?? false
            };
        }).ToList();
        var visiblePaths = files.Select(file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!product.IncludeSubdirectories || excludedDirectories.Count > 0)
        {
            // 不读取子目录或排除部分目录时保留历史映射，重新启用后可以恢复。
            visibleBindings.AddRange(previousBindings
                .Where(pair => !visiblePaths.Contains(pair.Key) &&
                    (!product.IncludeSubdirectories
                        ? pair.Key.Contains('/')
                        : IsPathExcluded(pair.Key, excludedDirectories)))
                .Select(pair => pair.Value));
        }

        product.FileBindings = visibleBindings;

        return files;
    }

    public IReadOnlyList<string> BindImportedFilesToHotspot(ProductConfig product, string modelRoot,
        IEnumerable<string> importedPaths, PartHotspot hotspot)
    {
        var imported = importedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupKeys = Scan(product, modelRoot)
            .Where(file => imported.Contains(Path.GetFullPath(file.FullPath)))
            .Select(file => file.GroupKey)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var groupKey in groupKeys)
        {
            if (!hotspot.GroupKeys.Contains(groupKey, StringComparer.CurrentCultureIgnoreCase))
            {
                hotspot.GroupKeys.Add(groupKey);
            }
        }

        return groupKeys;
    }

    public static IReadOnlyList<string> UnbindGroupsFromHotspot(PartHotspot hotspot, IEnumerable<string> groupKeys)
    {
        var requested = groupKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var removed = hotspot.GroupKeys
            .Where(requested.Contains)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        hotspot.GroupKeys.RemoveAll(requested.Contains);
        return removed;
    }

    public static string NormalizeRelative(string path) => path.Replace('\\', '/');

    public static string NormalizeExcludedDirectory(string path) =>
        NormalizeRelative(path).Trim().Trim('/');

    public static bool IsPathExcluded(string relativePath, IEnumerable<string> excludedDirectories)
    {
        var normalizedPath = NormalizeRelative(relativePath).TrimStart('/');
        foreach (var rawDirectory in excludedDirectories)
        {
            var directory = NormalizeExcludedDirectory(rawDirectory);
            if (string.IsNullOrWhiteSpace(directory) || directory == "." || directory == ".." ||
                directory.StartsWith("../", StringComparison.Ordinal))
            {
                continue;
            }

            if (normalizedPath.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsHistoryPath(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath).Trim().TrimStart('/');
        var gcodeHistory = GcodeDirectoryName + "/" + HistoryDirectoryName;
        return normalized.Equals(HistoryDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(HistoryDirectoryName + "/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(gcodeHistory, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(gcodeHistory + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateModelFiles(string modelRoot, bool includeSubdirectories,
        IReadOnlyCollection<string> excludedDirectories)
    {
        foreach (var path in Directory.EnumerateFiles(modelRoot, "*.3mf", SearchOption.TopDirectoryOnly)
                     .Where(path => !IsGcode3mf(path)))
        {
            yield return path;
        }

        var gcodeRoot = Path.Combine(modelRoot, GcodeDirectoryName);
        if (Directory.Exists(gcodeRoot))
        {
            foreach (var path in Directory.EnumerateFiles(gcodeRoot, "*.3mf", SearchOption.TopDirectoryOnly)
                         .Where(IsGcode3mf))
            {
                yield return path;
            }
        }

        if (!includeSubdirectories)
        {
            yield break;
        }

        var pending = new Stack<string>(Directory.EnumerateDirectories(modelRoot));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var relativeDirectory = NormalizeRelative(Path.GetRelativePath(modelRoot, directory));
            if (relativeDirectory.Equals(GcodeDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                IsHistoryPath(relativeDirectory) || IsPathExcluded(relativeDirectory, excludedDirectories))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.3mf", SearchOption.TopDirectoryOnly)
                         .Where(path => !IsGcode3mf(path)))
            {
                yield return path;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                pending.Push(child);
            }
        }
    }

    public static string GetDisplayName(string path)
    {
        var fileName = Path.GetFileName(path);
        var suffix = Gcode3mfSuffixRegex().Match(fileName);
        return suffix.Success ? fileName[..suffix.Index] : fileName;
    }

    public static bool IsGcode3mf(string path) => Gcode3mfSuffixRegex().IsMatch(Path.GetFileName(path));

    public static bool MatchesDisplayFilter(string path, ModelFileDisplayFilter filter)
    {
        if (!Path.GetFileName(path).EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return filter switch
        {
            ModelFileDisplayFilter.Gcode3mf => IsGcode3mf(path),
            ModelFileDisplayFilter.Standard3mf => !IsGcode3mf(path),
            _ => true
        };
    }

    [GeneratedRegex(@"[-_ ]*(?:v\d+_)?\d+(?:\.\d+)?h_\d+(?:\.\d+)?g\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedGcodeMetadataSuffixRegex();

    [GeneratedRegex(@"[-_ ]*\d+\s*(?:个|对|把|只|套|件|颗|张|枚|根|组|双|份|块)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantitySuffixRegex();

    [GeneratedRegex(@"[-_ ]+(?:v(?:er)?\.?\s*)?\d+(?:\.\d+)*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingVersionRegex();

    [GeneratedRegex(@"[-_ ]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\.gcode(?:\s*\(\d+\))?\.3mf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Gcode3mfSuffixRegex();
}
