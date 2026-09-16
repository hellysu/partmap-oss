namespace PartMap.Models;

public sealed class PortableProductPackage
{
    public int PackageVersion { get; set; } = 1;
    public DateTimeOffset WrittenUtc { get; set; } = DateTimeOffset.UtcNow;
    public ProductConfig Product { get; set; } = new();
    public string DiagramFileName { get; set; } = "diagram.png";
    public string DiagramBase64 { get; set; } = string.Empty;
}
