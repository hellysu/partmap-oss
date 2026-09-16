using System.Windows.Media.Imaging;

namespace PartMap.Models;

public enum ModelFileDisplayFilter
{
    All,
    Gcode3mf,
    Standard3mf
}

public sealed class ModelFile
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required string DisplayName { get; set; }
    public required string GroupKey { get; set; }
    public DateTime LastModifiedLocal { get; init; }
    public string LastModifiedText => $"最后修改：{LastModifiedLocal:yyyy-MM-dd HH:mm}";
    public BitmapSource? Thumbnail { get; set; }
}

public sealed class FileGroup
{
    public required string Key { get; init; }
    public required IReadOnlyList<ModelFile> Files { get; init; }
    public bool IsMapped { get; set; }
    public string DisplayText => $"{Key}  ({Files.Count})";
    public string MappingStatus => IsMapped ? "已分类" : "待归类";
    public override string ToString() => DisplayText;
}
