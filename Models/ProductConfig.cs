using System.Text.Json.Serialization;

namespace PartMap.Models;

public sealed class ProductConfig
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DiagramPath { get; set; } = string.Empty;
    public string ModelRoot { get; set; } = string.Empty;
    public bool IncludeSubdirectories { get; set; }
    public List<string> ExcludedDirectories { get; set; } = [];
    public List<PartHotspot> Hotspots { get; set; } = [];
    public List<FileBinding> FileBindings { get; set; } = [];
    public List<KeywordRule> KeywordRules { get; set; } = [];

    [JsonIgnore]
    public string ConfigPath { get; set; } = string.Empty;
}

public sealed class PartHotspot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新部件";
    public double X { get; set; } = 0.1;
    public double Y { get; set; } = 0.1;
    public double Width { get; set; } = 0.14;
    public double Height { get; set; } = 0.1;
    public List<string> GroupKeys { get; set; } = [];
}

public sealed class FileBinding
{
    public string RelativePath { get; set; } = string.Empty;
    public string GroupKey { get; set; } = string.Empty;
    public bool ManualOverride { get; set; }
}

public sealed class KeywordRule
{
    public string Pattern { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public bool IsRegex { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string? CurrentProductId { get; set; }
}

public sealed class ProductSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ConfigPath { get; init; }
    public override string ToString() => Name;
}
