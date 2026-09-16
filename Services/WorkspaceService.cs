using PartMap.Models;

namespace PartMap.Services;

public sealed class WorkspaceService
{
    private readonly ConfigStore _configStore;
    private readonly PortableProductPackageService _portablePackageService = new();

    public WorkspaceService(ConfigStore configStore, string? portableRoot = null)
    {
        _configStore = configStore;
        PortableRoot = Path.GetFullPath(portableRoot ?? AppContext.BaseDirectory);
        ProductsRoot = Path.Combine(PortableRoot, "products");
        var machineName = string.Concat(Environment.MachineName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        SettingsPath = Path.Combine(PortableRoot, $"settings.{machineName}.json");
        LegacySettingsPath = Path.Combine(PortableRoot, "settings.json");
        Directory.CreateDirectory(ProductsRoot);
    }

    public string PortableRoot { get; }
    public string ProductsRoot { get; }
    public string SettingsPath { get; }
    public string LegacySettingsPath { get; }

    public IReadOnlyList<ProductSummary> ListProducts()
    {
        return Directory.EnumerateFiles(ProductsRoot, "product.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                try
                {
                    var product = _configStore.Load<ProductConfig>(path);
                    return product is null ? null : new ProductSummary
                    {
                        Id = product.Id,
                        Name = product.Name,
                        ConfigPath = path
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(item => item is not null)
            .Cast<ProductSummary>()
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public ProductConfig? LoadProduct(ProductSummary summary)
    {
        var product = _configStore.Load<ProductConfig>(summary.ConfigPath);
        if (product is not null)
        {
            product.Hotspots ??= [];
            product.FileBindings ??= [];
            product.KeywordRules ??= [];
            product.ExcludedDirectories ??= [];
            product.ConfigPath = summary.ConfigPath;
        }

        return product;
    }

    public ProductConfig CreateProduct(string name, string imagePath, string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("产品名称不能为空。", nameof(name));
        }

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("找不到包装图。", imagePath);
        }

        if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException("请选择有效的模型目录。");
        }

        ProductConfig? product = null;
        _configStore.ExecuteLocked(Path.Combine(ProductsRoot, ".catalog"), () =>
        {
            var id = CreateUniqueId(name);
            var productDirectory = Path.Combine(ProductsRoot, id);
            Directory.CreateDirectory(productDirectory);

            var imageExtension = Path.GetExtension(imagePath).ToLowerInvariant();
            var diagramDestination = Path.Combine(productDirectory, "diagram" + imageExtension);
            File.Copy(imagePath, diagramDestination, true);

            var configPath = Path.Combine(productDirectory, "product.json");
            product = new ProductConfig
            {
                Id = id,
                Name = name.Trim(),
                DiagramPath = FileGroupingService.NormalizeRelative(Path.GetRelativePath(PortableRoot, diagramDestination)),
                ModelRoot = ToConfiguredPath(modelDirectory),
                ConfigPath = configPath
            };
            SaveProduct(product);
        });
        return product!;
    }

    public void SaveProduct(ProductConfig product)
    {
        if (string.IsNullOrWhiteSpace(product.ConfigPath))
        {
            product.ConfigPath = Path.Combine(ProductsRoot, product.Id, "product.json");
        }

        _configStore.ExecuteLocked(product.ConfigPath, () =>
        {
            var current = _configStore.Load<ProductConfig>(product.ConfigPath);
            if (current is not null && current.Revision != product.Revision)
            {
                throw new ProductConfigConflictException();
            }

            var previousRevision = product.Revision;
            product.Revision = (current?.Revision ?? previousRevision) + 1;
            try
            {
                _configStore.SaveUnlocked(product.ConfigPath, product);
            }
            catch
            {
                product.Revision = previousRevision;
                throw;
            }
        });

        try
        {
            SyncPortableProduct(product);
        }
        catch (Exception exception)
        {
            throw new IOException(
                $"产品配置已保存，但无法更新模型目录中的 {PortableProductPackageService.ConfigDirectoryName}\\{PortableProductPackageService.ConfigFileName}。请检查该目录的写入权限。",
                exception);
        }
    }

    public AppSettings LoadSettings() =>
        _configStore.Load<AppSettings>(SettingsPath) ??
        _configStore.Load<AppSettings>(LegacySettingsPath) ??
        new AppSettings();
    public void SaveSettings(AppSettings settings) => _configStore.Save(SettingsPath, settings);

    public bool SyncPortableProduct(ProductConfig product)
    {
        var modelRoot = ResolvePortablePath(product.ModelRoot);
        var diagramPath = ResolvePortablePath(product.DiagramPath);
        var delays = new[] { 120, 300 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _portablePackageService.Save(product, modelRoot, diagramPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < delays.Length)
            {
                Thread.Sleep(delays[attempt]);
            }
        }
    }

    public ProductConfig ImportProductFolder(string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException("请选择有效的产品 3MF 文件夹。");
        }

        modelDirectory = Path.GetFullPath(modelDirectory);
        var package = _portablePackageService.Load(modelDirectory);
        var diagramBytes = _portablePackageService.DecodeDiagram(package);
        ProductConfig? imported = null;

        _configStore.ExecuteLocked(Path.Combine(ProductsRoot, ".catalog"), () =>
        {
            var stableSelectedRoot = NetworkPathService.ToStablePath(modelDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var summary in ListProducts())
            {
                var existing = LoadProduct(summary);
                if (existing is null)
                {
                    continue;
                }

                var existingRoot = NetworkPathService.ToStablePath(ResolvePortablePath(existing.ModelRoot))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(existingRoot, stableSelectedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (package.Product.Revision <= existing.Revision)
                    {
                        imported = existing;
                        return;
                    }

                    var existingDirectory = Path.GetDirectoryName(existing.ConfigPath)!;
                    var existingDiagramDestination = Path.Combine(existingDirectory,
                        "diagram" + GetDiagramExtension(package.DiagramFileName));
                    var refreshed = package.Product;
                    refreshed.Id = existing.Id;
                    refreshed.DiagramPath = FileGroupingService.NormalizeRelative(
                        Path.GetRelativePath(PortableRoot, existingDiagramDestination));
                    refreshed.ModelRoot = ToConfiguredPath(modelDirectory);
                    refreshed.ConfigPath = existing.ConfigPath;
                    EnsureCollections(refreshed);

                    _configStore.ExecuteLocked(existing.ConfigPath, () =>
                    {
                        var current = _configStore.Load<ProductConfig>(existing.ConfigPath);
                        if (current is not null && current.Revision != existing.Revision)
                        {
                            throw new ProductConfigConflictException();
                        }

                        WriteBytesAtomic(existingDiagramDestination, diagramBytes);
                        _configStore.SaveUnlocked(existing.ConfigPath, refreshed);
                    });
                    imported = refreshed;
                    return;
                }
            }

            var id = CreateUniqueId(package.Product.Name);
            var productDirectory = Path.Combine(ProductsRoot, id);
            Directory.CreateDirectory(productDirectory);
            var diagramDestination = Path.Combine(productDirectory,
                "diagram" + GetDiagramExtension(package.DiagramFileName));
            WriteBytesAtomic(diagramDestination, diagramBytes);
            var product = package.Product;
            product.Id = id;
            product.DiagramPath = FileGroupingService.NormalizeRelative(
                Path.GetRelativePath(PortableRoot, diagramDestination));
            product.ModelRoot = ToConfiguredPath(modelDirectory);
            product.ConfigPath = Path.Combine(productDirectory, "product.json");
            EnsureCollections(product);
            SaveProduct(product);
            imported = product;
        });

        return imported!;
    }

    public string ResolvePortablePath(string configuredPath)
    {
        var platformPath = configuredPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(platformPath)
            ? Path.GetFullPath(platformPath)
            : Path.GetFullPath(Path.Combine(PortableRoot, platformPath));
    }

    public string ToConfiguredPath(string path)
    {
        var absolute = NetworkPathService.ToStablePath(path);
        var stablePortableRoot = NetworkPathService.ToStablePath(PortableRoot);
        var relative = Path.GetRelativePath(stablePortableRoot, absolute);
        var outsidePortableRoot = relative.Equals("..", StringComparison.Ordinal) ||
                                  relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                                  Path.IsPathRooted(relative);
        return FileGroupingService.NormalizeRelative(outsidePortableRoot ? absolute : relative);
    }

    public void RebindModelDirectory(ProductConfig product, string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
        {
            throw new DirectoryNotFoundException("选择的模型目录不存在。");
        }

        product.ModelRoot = ToConfiguredPath(modelDirectory);
        SaveProduct(product);
    }

    public void RenameProduct(ProductConfig product, string newName)
    {
        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("产品名称不能为空。");
        }

        product.Name = trimmed;
        SaveProduct(product);
    }

    public void DeleteProduct(ProductConfig product)
    {
        if (string.IsNullOrWhiteSpace(product.ConfigPath))
            throw new InvalidOperationException("产品配置路径无效。");

        var productDirectory = Path.GetFullPath(Path.GetDirectoryName(product.ConfigPath)!);
        var productsRoot = Path.GetFullPath(ProductsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!productDirectory.StartsWith(productsRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("只能删除 PartMap 本机产品记录。");

        _configStore.ExecuteLocked(Path.Combine(ProductsRoot, ".catalog"), () =>
        {
            if (Directory.Exists(productDirectory)) Directory.Delete(productDirectory, true);
        });
    }

    public ProductConfig UpdateFileBindingAfterRename(ProductConfig observedProduct, string oldRelativePath,
        string newRelativePath)
    {
        if (string.IsNullOrWhiteSpace(observedProduct.ConfigPath))
        {
            throw new InvalidOperationException("产品配置路径无效。");
        }

        ProductConfig? updated = null;
        _configStore.ExecuteLocked(observedProduct.ConfigPath, () =>
        {
            var latest = _configStore.Load<ProductConfig>(observedProduct.ConfigPath) ?? observedProduct;
            var oldNormalized = FileGroupingService.NormalizeRelative(oldRelativePath);
            var newNormalized = FileGroupingService.NormalizeRelative(newRelativePath);
            var binding = latest.FileBindings.FirstOrDefault(item =>
                string.Equals(FileGroupingService.NormalizeRelative(item.RelativePath), oldNormalized,
                    StringComparison.OrdinalIgnoreCase));
            var observedBinding = observedProduct.FileBindings.FirstOrDefault(item =>
                string.Equals(FileGroupingService.NormalizeRelative(item.RelativePath), oldNormalized,
                    StringComparison.OrdinalIgnoreCase));

            latest.FileBindings.RemoveAll(item => !ReferenceEquals(item, binding) &&
                string.Equals(FileGroupingService.NormalizeRelative(item.RelativePath), newNormalized,
                    StringComparison.OrdinalIgnoreCase));
            if (binding is not null)
            {
                binding.RelativePath = newNormalized;
            }
            else if (observedBinding is not null)
            {
                latest.FileBindings.Add(new FileBinding
                {
                    RelativePath = newNormalized,
                    GroupKey = observedBinding.GroupKey,
                    ManualOverride = observedBinding.ManualOverride
                });
            }

            latest.Revision++;
            latest.ConfigPath = observedProduct.ConfigPath;
            _configStore.SaveUnlocked(observedProduct.ConfigPath, latest);
            updated = latest;
        });
        SyncPortableProduct(updated!);
        return updated!;
    }

    public ProductConfig UpsertFileBinding(ProductConfig observedProduct, string relativePath, string groupKey) =>
        UpsertFileBindings(observedProduct, [(relativePath, groupKey)]);

    public ProductConfig UpsertFileBindings(ProductConfig observedProduct,
        IReadOnlyList<(string RelativePath, string GroupKey)> updates)
    {
        if (string.IsNullOrWhiteSpace(observedProduct.ConfigPath))
            throw new InvalidOperationException("产品配置路径无效。");
        if (updates.Count == 0) return observedProduct;
        if (updates.Any(item => string.IsNullOrWhiteSpace(item.GroupKey)))
            throw new ArgumentException("文件组不能为空。", nameof(updates));

        ProductConfig? updated = null;
        _configStore.ExecuteLocked(observedProduct.ConfigPath, () =>
        {
            var latest = _configStore.Load<ProductConfig>(observedProduct.ConfigPath) ?? observedProduct;
            EnsureCollections(latest);
            foreach (var update in updates)
            {
                var normalized = FileGroupingService.NormalizeRelative(update.RelativePath);
                var binding = latest.FileBindings.FirstOrDefault(item =>
                    string.Equals(FileGroupingService.NormalizeRelative(item.RelativePath), normalized,
                        StringComparison.OrdinalIgnoreCase));
                if (binding is null)
                {
                    latest.FileBindings.Add(new FileBinding
                    {
                        RelativePath = normalized,
                        GroupKey = update.GroupKey,
                        ManualOverride = true
                    });
                }
                else
                {
                    binding.GroupKey = update.GroupKey;
                    binding.ManualOverride = true;
                }
            }

            latest.Revision++;
            latest.ConfigPath = observedProduct.ConfigPath;
            _configStore.SaveUnlocked(observedProduct.ConfigPath, latest);
            updated = latest;
        });
        SyncPortableProduct(updated!);
        return updated!;
    }

    private static string GetDiagramExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" ? extension : ".png";
    }

    private static void EnsureCollections(ProductConfig product)
    {
        product.Hotspots ??= [];
        product.FileBindings ??= [];
        product.KeywordRules ??= [];
        product.ExcludedDirectories ??= [];
    }

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string CreateUniqueId(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var baseId = new string(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "product";
        }

        var id = baseId;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(ProductsRoot, id)))
        {
            id = $"{baseId}-{suffix++}";
        }

        return id;
    }
}
