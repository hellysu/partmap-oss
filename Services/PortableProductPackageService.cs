using System.Text.Json;
using PartMap.Models;

namespace PartMap.Services;

public sealed class PortableProductPackageService
{
    public const string ConfigDirectoryName = "配置";
    public const string ConfigFileName = "PartMap.product.json";
    public const string DiagramBaseName = "包装图";
    private const int MaximumDiagramBytes = 50 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string GetConfigDirectoryPath(string modelRoot) =>
        Path.Combine(Path.GetFullPath(modelRoot), ConfigDirectoryName);

    public string GetConfigPath(string modelRoot) => Path.Combine(GetConfigDirectoryPath(modelRoot), ConfigFileName);

    private string GetLegacyConfigPath(string modelRoot) => Path.Combine(Path.GetFullPath(modelRoot), ConfigFileName);

    public PortableProductPackage Load(string modelRoot)
    {
        var configPath = GetConfigPath(modelRoot);
        if (!File.Exists(configPath)) configPath = GetLegacyConfigPath(modelRoot);
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"所选目录中没有 {ConfigDirectoryName}\\{ConfigFileName}。", configPath);
        }

        var package = JsonSerializer.Deserialize<PortableProductPackage>(File.ReadAllText(configPath), JsonOptions)
                      ?? throw new InvalidDataException("便携产品配置为空。");
        Validate(package);
        return package;
    }

    public bool Save(ProductConfig product, string modelRoot, string diagramPath)
    {
        modelRoot = Path.GetFullPath(modelRoot);
        if (!Directory.Exists(modelRoot))
        {
            throw new DirectoryNotFoundException($"模型目录不存在：{modelRoot}");
        }

        if (!File.Exists(diagramPath))
        {
            throw new FileNotFoundException("无法把包装图写入便携配置。", diagramPath);
        }

        var diagramBytes = File.ReadAllBytes(diagramPath);
        if (diagramBytes.Length > MaximumDiagramBytes)
        {
            throw new InvalidDataException("包装图超过 50 MB，无法写入便携配置。");
        }

        var configDirectory = GetConfigDirectoryPath(modelRoot);
        Directory.CreateDirectory(configDirectory);
        var configPath = GetConfigPath(modelRoot);
        var legacyConfigPath = GetLegacyConfigPath(modelRoot);
        var lockPath = configPath + ".lock";
        var written = true;
        try
        {
            using (AcquireLock(lockPath, TimeSpan.FromSeconds(8)))
            {
                var existingPath = File.Exists(configPath) ? configPath : legacyConfigPath;
                if (File.Exists(existingPath))
                {
                    try
                    {
                        var existing = JsonSerializer.Deserialize<PortableProductPackage>(File.ReadAllText(existingPath), JsonOptions);
                        if (existing?.Product.Revision > product.Revision)
                        {
                            if (string.Equals(existingPath, legacyConfigPath, StringComparison.OrdinalIgnoreCase) &&
                                !File.Exists(configPath))
                            {
                                MigrateLegacyPackage(existing, configDirectory, configPath, legacyConfigPath);
                            }
                            written = false;
                        }
                    }
                    catch (JsonException)
                    {
                        // 使用当前程序内的有效配置修复损坏的便携副本。
                    }
                }

                if (written)
                {
                    var diagramFileName = DiagramBaseName + NormalizeDiagramExtension(diagramPath);
                    var portableDiagramPath = Path.Combine(configDirectory, diagramFileName);
                    var portableProduct = CloneProduct(product);
                    portableProduct.ModelRoot = ".";
                    portableProduct.DiagramPath = FileGroupingService.NormalizeRelative(
                        Path.Combine(ConfigDirectoryName, diagramFileName));
                    var package = new PortableProductPackage
                    {
                        WrittenUtc = DateTimeOffset.UtcNow,
                        Product = portableProduct,
                        DiagramFileName = diagramFileName,
                        DiagramBase64 = Convert.ToBase64String(diagramBytes)
                    };
                    WriteBytesAtomic(portableDiagramPath, diagramBytes);
                    WriteAtomic(configPath, JsonSerializer.Serialize(package, JsonOptions));
                    EnsureVisible(configPath);
                    EnsureVisible(portableDiagramPath);
                    TryDeleteLegacyConfig(legacyConfigPath);
                }
            }
        }
        finally
        {
            TryDeleteLockFile(lockPath);
        }

        return written;
    }

    public byte[] DecodeDiagram(PortableProductPackage package)
    {
        Validate(package);
        try
        {
            var bytes = Convert.FromBase64String(package.DiagramBase64);
            if (bytes.Length > MaximumDiagramBytes)
            {
                throw new InvalidDataException("便携配置中的包装图超过 50 MB。");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("便携配置中的包装图数据无效。", exception);
        }
    }

    private static ProductConfig CloneProduct(ProductConfig product)
    {
        return JsonSerializer.Deserialize<ProductConfig>(JsonSerializer.Serialize(product, JsonOptions), JsonOptions)
               ?? throw new InvalidDataException("无法生成便携产品配置。");
    }

    private static void Validate(PortableProductPackage package)
    {
        if (package.PackageVersion != 1)
        {
            throw new InvalidDataException($"不支持的便携配置版本：{package.PackageVersion}。");
        }

        if (package.Product is null || string.IsNullOrWhiteSpace(package.Product.Name))
        {
            throw new InvalidDataException("便携配置缺少产品名称。");
        }

        if (string.IsNullOrWhiteSpace(package.DiagramBase64))
        {
            throw new InvalidDataException("便携配置缺少包装图数据。");
        }
    }

    private static FileStream AcquireLock(string lockPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                lastError = exception;
                Thread.Sleep(80);
            }
        }

        throw new TimeoutException("另一台电脑正在更新模型目录中的便携配置，请稍后重试。", lastError);
    }

    private static void WriteAtomic(string path, string json)
    {
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, json);
            try
            {
                File.Move(temporary, path, true);
            }
            catch (UnauthorizedAccessException) when (File.Exists(path))
            {
                OverwriteExistingLocked(path, json);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void OverwriteExistingLocked(string path, string json)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
        writer.Write(json);
        writer.Flush();
        stream.Flush(true);
    }

    private void MigrateLegacyPackage(PortableProductPackage package, string configDirectory,
        string configPath, string legacyConfigPath)
    {
        var diagramBytes = DecodeDiagram(package);
        var diagramFileName = DiagramBaseName + NormalizeDiagramExtension(package.DiagramFileName);
        var portableDiagramPath = Path.Combine(configDirectory, diagramFileName);
        package.Product.ModelRoot = ".";
        package.Product.DiagramPath = FileGroupingService.NormalizeRelative(
            Path.Combine(ConfigDirectoryName, diagramFileName));
        package.DiagramFileName = diagramFileName;
        WriteBytesAtomic(portableDiagramPath, diagramBytes);
        WriteAtomic(configPath, JsonSerializer.Serialize(package, JsonOptions));
        EnsureVisible(configPath);
        EnsureVisible(portableDiagramPath);
        TryDeleteLegacyConfig(legacyConfigPath);
    }

    private static string NormalizeDiagramExtension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" ? extension : ".png";
    }

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, path, true);
            }
            catch (UnauthorizedAccessException) when (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.SetLength(0);
                stream.Write(bytes);
                stream.Flush(true);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureVisible(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Hidden) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.Hidden);
    }

    private static void TryDeleteLegacyConfig(string path)
    {
        FileAttributes? originalAttributes = null;
        try
        {
            if (!File.Exists(path)) return;
            originalAttributes = File.GetAttributes(path);
            if ((originalAttributes.Value & FileAttributes.Hidden) != 0)
                File.SetAttributes(path, originalAttributes.Value & ~FileAttributes.Hidden);
            File.Delete(path);
        }
        catch (IOException)
        {
            RestoreAttributesIfPresent(path, originalAttributes);
        }
        catch (UnauthorizedAccessException)
        {
            RestoreAttributesIfPresent(path, originalAttributes);
        }
    }

    private static void RestoreAttributesIfPresent(string path, FileAttributes? attributes)
    {
        if (!attributes.HasValue || !File.Exists(path)) return;
        try { File.SetAttributes(path, attributes.Value); } catch { }
    }

    private static void TryDeleteLockFile(string lockPath)
    {
        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
