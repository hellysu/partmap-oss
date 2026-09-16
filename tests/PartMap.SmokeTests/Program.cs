using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using PartMap.Controls;
using PartMap.Models;
using PartMap.Services;

var tests = new (string Name, Action Run)[]
{
    ("中文文件名自动分组", TestGrouping),
    ("子目录读取开关与产品改名", TestProductOptions),
    ("模型目录单文件配置与跨电脑恢复", TestWorkspaceAndScan),
    ("删除产品只删除本机记录", TestDeleteProductKeepsModelFiles),
    ("旧版较新共享配置迁移", TestNewerLegacyPortableMigration),
    ("未分类文件组过滤", TestGroupFiltering),
    ("透明热点浏览与编辑状态", TestHotspotPresentation),
    ("配置原子保存与备份恢复", TestConfigBackup),
    ("双实例配置锁与版本冲突保护", TestConcurrentConfiguration),
    ("真实文件重命名与冲突检查", TestRename),
    ("外部 3MF 拖入复制与修改日期", TestFileImport),
    ("历史归档日期命名与版本冲突", TestArchiveToHistory),
    ("G-code目录与一键切片自动版本", TestOneClickSlicing),
    ("切片时间克重向上取整", TestSliceSummaryRounding),
    ("多材料克重按当前板总和", TestMultiMaterialWeightSum),
    ("多板读取自定义板名", TestPlateMetadataRead),
    ("指定板切片传递板ID", TestPlateSpecificSliceArguments),
    ("旧G-code只留完整参数记录", TestSliceParameterHistory),
    ("多板按自定义板名独立版本", TestNamedPlateSeries),
    ("一键切片传递自定义板", TestOneClickNamedPlate),
    ("优先使用正在运行的 Bambu Studio", TestBambuStudioSelection),
    ("仅剩G-code时跨电脑恢复热点归属", TestGcodeOnlyRestore),
    ("删除文件目录边界与刷新输入", TestDeleteSafety),
    ("共享配置瞬时占用自动重试", TestTransientPortableConfigRetry),
    ("共享3MF缩略图读取不阻塞写入", TestThumbnailReadWhileWritable),
    ("批量切片严格串行且失败后继续", TestBatchSlicingQueue),
    ("批量切片映射单次保存", TestBatchBindingUpdate),
    ("网络共享路径识别", TestNetworkPathDetection)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL  {test.Name}");
        Console.WriteLine(exception);
    }
}

Console.WriteLine($"\n{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static void TestGrouping()
{
    var service = new FileGroupingService();
    var product = new ProductConfig { Name = "示例机器人" };
    Equal("头盔", service.DeriveGroupKey("示例机器人-头盔-10个.3mf", product));
    Equal("肩膀", service.DeriveGroupKey("H2D-示例机器人-肩膀-14对.3mf", product));
    Equal("肩膀", service.DeriveGroupKey("H2D示例机器人-肩膀-14对.3mf", product));
    Equal("底座", service.DeriveGroupKey("P1S-示例机器人-底座-5个.3mf", product));
    Equal("软胶手", service.DeriveGroupKey("示例机器人-软胶手-25对.3mf", product));
    Equal("头盔", service.DeriveGroupKey("示例机器人-头盔-10个.gcode.3mf", product));
    Equal("示例机器人-头盔-10个", FileGroupingService.GetDisplayName("示例机器人-头盔-10个.gcode.3mf"));
    Equal("示例机器人-头盔-10个", FileGroupingService.GetDisplayName("示例机器人-头盔-10个.gcode(1).3mf"));
    Equal("示例机器人-头盔-10个", FileGroupingService.GetDisplayName("示例机器人-头盔-10个.gcode (12).3mf"));
    Equal("示例机器人-头盔-10个.3mf", FileGroupingService.GetDisplayName("示例机器人-头盔-10个.3mf"));
    True(FileGroupingService.IsGcode3mf("示例机器人-头盔-10个.gcode.3mf"));
    True(FileGroupingService.IsGcode3mf("示例机器人-头盔-10个.gcode(1).3mf"));
    True(!FileGroupingService.IsGcode3mf("示例机器人-头盔-10个.3mf"));
    Equal("头盔", service.DeriveGroupKey("示例机器人-头盔-10个.gcode(1).3mf", product));
    True(FileGroupingService.MatchesDisplayFilter(
        "示例机器人-头盔-10个.gcode.3mf", ModelFileDisplayFilter.Gcode3mf));
    True(!FileGroupingService.MatchesDisplayFilter(
        "示例机器人-头盔-10个.gcode.3mf", ModelFileDisplayFilter.Standard3mf));
    True(FileGroupingService.MatchesDisplayFilter(
        "示例机器人-头盔-10个.3mf", ModelFileDisplayFilter.Standard3mf));
    True(!FileGroupingService.MatchesDisplayFilter(
        "说明.txt", ModelFileDisplayFilter.All));

    product.KeywordRules.Add(new KeywordRule { Pattern = "装饰蓝", Replacement = string.Empty });
    Equal("头盔", service.DeriveGroupKey("示例机器人-头盔装饰蓝-100个.3mf", product));
}

static void TestProductOptions()
{
    WithTemporaryDirectory(root =>
    {
        var image = Path.Combine(root, "source.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="));
        var models = Path.Combine(root, "models");
        var nested = Path.Combine(models, "批次A");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(models, "产品-头盔-1个.3mf"), []);
        File.WriteAllBytes(Path.Combine(nested, "产品-肩膀-1对.3mf"), []);

        var workspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "portable"));
        var product = workspace.CreateProduct("产品", image, models);
        var grouping = new FileGroupingService();
        True(!product.IncludeSubdirectories);
        Equal(1, grouping.Scan(product, models).Count);

        product.IncludeSubdirectories = true;
        var recursive = grouping.Scan(product, models);
        Equal(2, recursive.Count);
        var nestedBinding = product.FileBindings.Single(binding => binding.RelativePath.Contains('/'));
        nestedBinding.GroupKey = "人工肩膀";
        nestedBinding.ManualOverride = true;

        product.IncludeSubdirectories = false;
        Equal(1, grouping.Scan(product, models).Count);
        True(product.FileBindings.Any(binding =>
            binding.RelativePath.Contains('/') && binding.GroupKey == "人工肩膀" && binding.ManualOverride));

        product.IncludeSubdirectories = true;
        var recursiveAgain = grouping.Scan(product, models);
        Equal("人工肩膀", recursiveAgain.Single(file => file.RelativePath.Contains('/')).GroupKey);

        product.ExcludedDirectories = ["批次A"];
        Equal(1, grouping.Scan(product, models).Count);
        True(product.FileBindings.Any(binding =>
            binding.RelativePath.Contains('/') && binding.GroupKey == "人工肩膀" && binding.ManualOverride));
        True(FileGroupingService.IsPathExcluded("批次A/产品-肩膀-1对.3mf", product.ExcludedDirectories));
        True(FileGroupingService.IsPathExcluded("批次A", product.ExcludedDirectories));
        True(!FileGroupingService.IsPathExcluded("批次B/产品-肩膀-1对.3mf", product.ExcludedDirectories));
        product.ExcludedDirectories.Clear();
        Equal("人工肩膀", grouping.Scan(product, models).Single(file => file.RelativePath.Contains('/')).GroupKey);

        var hotspot = new PartHotspot { Name = "肩膀", GroupKeys = ["人工肩膀", "宝石肩膀"] };
        var removed = FileGroupingService.UnbindGroupsFromHotspot(hotspot, ["人工肩膀"]);
        Equal(1, removed.Count);
        True(!hotspot.GroupKeys.Contains("人工肩膀"));
        True(hotspot.GroupKeys.Contains("宝石肩膀"));

        workspace.RenameProduct(product, "新产品名称");
        Equal("新产品名称", workspace.ListProducts().Single().Name);
        Throws<InvalidOperationException>(() => workspace.RenameProduct(product, "   "));

        var legacyPath = Path.Combine(root, "legacy.json");
        File.WriteAllText(legacyPath, "{\"Name\":\"旧配置\"}");
        var legacy = new ConfigStore().Load<ProductConfig>(legacyPath)!;
        True(!legacy.IncludeSubdirectories);
    });
}

static void TestWorkspaceAndScan()
{
    WithTemporaryDirectory(root =>
    {
        var image = Path.Combine(root, "source.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="));
        var sourceModels = Path.Combine(root, "incoming");
        Directory.CreateDirectory(sourceModels);
        File.WriteAllBytes(Path.Combine(sourceModels, "示例机器人-头盔-10个.3mf"), []);
        File.WriteAllBytes(Path.Combine(sourceModels, "H2D-示例机器人-肩膀-14对.3mf"), []);

        var portable = Path.Combine(root, "portable");
        var store = new ConfigStore();
        var workspace = new WorkspaceService(store, portable);
        var product = workspace.CreateProduct("示例机器人", image, sourceModels);
        var grouping = new FileGroupingService();
        var files = grouping.Scan(product, workspace.ResolvePortablePath(product.ModelRoot));

        Equal(2, files.Count);
        True(product.DiagramPath.StartsWith("products/", StringComparison.Ordinal));
        True(File.Exists(workspace.ResolvePortablePath(product.DiagramPath)));
        True(Path.IsPathRooted(product.ModelRoot));
        Equal(Path.GetFullPath(sourceModels), workspace.ResolvePortablePath(product.ModelRoot));
        True(!Directory.Exists(Path.Combine(portable, "models")));
        True(files.Any(file => file.GroupKey == "头盔"));
        True(files.Any(file => file.GroupKey == "肩膀"));
        product.Hotspots.Add(new PartHotspot { Name = "头盔位置", GroupKeys = ["头盔"] });
        workspace.SaveProduct(product);

        var portableConfigDirectory = Path.Combine(sourceModels, "配置");
        var portableConfigPath = Path.Combine(portableConfigDirectory, PortableProductPackageService.ConfigFileName);
        var portableDiagramPath = Path.Combine(portableConfigDirectory, "包装图.png");
        True(File.Exists(portableConfigPath));
        True(File.Exists(portableDiagramPath));
        True((File.GetAttributes(portableConfigPath) & FileAttributes.Hidden) == 0);
        True((File.GetAttributes(portableDiagramPath) & FileAttributes.Hidden) == 0);
        True(!File.Exists(Path.Combine(sourceModels, PortableProductPackageService.ConfigFileName)));
        var portableJson = File.ReadAllText(portableConfigPath);
        True(portableJson.Contains("\"ModelRoot\": \".\"", StringComparison.Ordinal));
        True(portableJson.Contains("DiagramBase64", StringComparison.Ordinal));
        Equal(0, Directory.EnumerateFiles(portableConfigDirectory, "*.lock", SearchOption.TopDirectoryOnly).Count());

        var copiedModels = Path.Combine(root, "copied-to-offline-computer");
        Directory.CreateDirectory(copiedModels);
        foreach (var file in Directory.EnumerateFiles(sourceModels, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceModels, file);
            var destination = Path.Combine(copiedModels, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var offlinePortable = Path.Combine(root, "offline-PartMap");
        var offlineWorkspace = new WorkspaceService(new ConfigStore(), offlinePortable);
        var imported = offlineWorkspace.ImportProductFolder(copiedModels);
        Equal("示例机器人", imported.Name);
        Equal(Path.GetFullPath(copiedModels), offlineWorkspace.ResolvePortablePath(imported.ModelRoot));
        True(imported.Hotspots.Any(item => item.Name == "头盔位置" && item.GroupKeys.Contains("头盔")));
        True(imported.FileBindings.Any(item => item.RelativePath == "示例机器人-头盔-10个.3mf"));
        True(File.Exists(offlineWorkspace.ResolvePortablePath(imported.DiagramPath)));
        Equal(2, grouping.Scan(imported, offlineWorkspace.ResolvePortablePath(imported.ModelRoot)).Count);

        var importedId = imported.Id;
        var secondOfflinePortable = Path.Combine(root, "second-offline-PartMap");
        var secondOfflineWorkspace = new WorkspaceService(new ConfigStore(), secondOfflinePortable);
        var secondImported = secondOfflineWorkspace.ImportProductFolder(copiedModels);
        secondImported.Hotspots.Add(new PartHotspot { Name = "第二台电脑新增热点", GroupKeys = ["人工肩膀"] });
        var secondShoulderBinding = secondImported.FileBindings.Single(binding =>
            binding.RelativePath == "H2D-示例机器人-肩膀-14对.3mf");
        secondShoulderBinding.GroupKey = "人工肩膀";
        secondShoulderBinding.ManualOverride = true;
        secondOfflineWorkspace.SaveProduct(secondImported);

        var refreshed = offlineWorkspace.ImportProductFolder(copiedModels);
        Equal(importedId, refreshed.Id);
        Equal(secondImported.Revision, refreshed.Revision);
        True(refreshed.Hotspots.Any(item => item.Name == "第二台电脑新增热点"));
        Equal("人工肩膀", refreshed.FileBindings.Single(binding =>
            binding.RelativePath == "H2D-示例机器人-肩膀-14对.3mf").GroupKey);
        Equal("人工肩膀", grouping.Scan(refreshed, copiedModels).Single(file =>
            file.RelativePath == "H2D-示例机器人-肩膀-14对.3mf").GroupKey);

        var legacyImportRoot = Path.Combine(root, "legacy-layout");
        Directory.CreateDirectory(legacyImportRoot);
        File.Copy(Path.Combine(sourceModels, "示例机器人-头盔-10个.3mf"),
            Path.Combine(legacyImportRoot, "示例机器人-头盔-10个.3mf"));
        File.Copy(portableConfigPath, Path.Combine(legacyImportRoot, PortableProductPackageService.ConfigFileName));
        var legacyWorkspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "legacy-offline-PartMap"));
        var legacyImported = legacyWorkspace.ImportProductFolder(legacyImportRoot);
        Equal("示例机器人", legacyImported.Name);
        True(File.Exists(Path.Combine(legacyImportRoot, "配置", PortableProductPackageService.ConfigFileName)));
        True(File.Exists(Path.Combine(legacyImportRoot, "配置", "包装图.png")));
        True(!File.Exists(Path.Combine(legacyImportRoot, PortableProductPackageService.ConfigFileName)));

        var summary = workspace.ListProducts().Single();
        var loaded = workspace.LoadProduct(summary)!;
        Equal("示例机器人", loaded.Name);
        True(!Path.IsPathRooted(loaded.DiagramPath));
        True(Path.IsPathRooted(loaded.ModelRoot));

        var portableModels = Path.Combine(portable, "已有模型");
        Directory.CreateDirectory(portableModels);
        Equal("已有模型", workspace.ToConfiguredPath(portableModels));

        var replacement = Path.Combine(root, "replacement");
        Directory.CreateDirectory(replacement);
        File.WriteAllBytes(Path.Combine(replacement, "示例机器人-头盔-10个.3mf"), []);
        product.FileBindings.Single(binding => binding.RelativePath == "示例机器人-头盔-10个.3mf").GroupKey = "人工头盔组";
        workspace.RebindModelDirectory(product, replacement);
        var reboundFiles = grouping.Scan(product, workspace.ResolvePortablePath(product.ModelRoot));
        Equal("人工头盔组", reboundFiles.Single().GroupKey);
        True(File.Exists(Path.Combine(sourceModels, "示例机器人-头盔-10个.3mf")));

        var missing = Path.Combine(root, "missing-model-directory");
        product.ModelRoot = workspace.ToConfiguredPath(missing);
        Throws<DirectoryNotFoundException>(() =>
            grouping.Scan(product, workspace.ResolvePortablePath(product.ModelRoot)));
        True(!Directory.Exists(missing));

        var legacyRoot = Path.Combine(portable, "models", "legacy");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllBytes(Path.Combine(legacyRoot, "旧版-底座-5个.3mf"), []);
        var legacyProduct = new ProductConfig { Name = "旧版", ModelRoot = "models/legacy" };
        Equal(1, grouping.Scan(legacyProduct, workspace.ResolvePortablePath(legacyProduct.ModelRoot)).Count);
    });
}

static void TestDeleteProductKeepsModelFiles()
{
    WithTemporaryDirectory(root =>
    {
        var image = Path.Combine(root, "source.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="));
        var models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);
        var modelFile = Path.Combine(models, "误建产品-零件.3mf");
        File.WriteAllBytes(modelFile, [1, 2, 3]);
        var workspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "PartMap"));
        var product = workspace.CreateProduct("误建产品", image, models);
        var localProductDirectory = Path.GetDirectoryName(product.ConfigPath)!;
        var sharedConfig = Path.Combine(models, PortableProductPackageService.ConfigDirectoryName,
            PortableProductPackageService.ConfigFileName);
        True(File.Exists(sharedConfig));

        workspace.DeleteProduct(product);

        True(!Directory.Exists(localProductDirectory));
        Equal(0, workspace.ListProducts().Count);
        True(File.Exists(modelFile));
        True(File.Exists(sharedConfig));
    });
}

static void TestConfigBackup()
{
    WithTemporaryDirectory(root =>
    {
        var store = new ConfigStore();
        var path = Path.Combine(root, "settings.json");
        store.Save(path, new AppSettings { CurrentProductId = "first" });
        store.Save(path, new AppSettings { CurrentProductId = "second" });
        File.WriteAllText(path, "{broken json");
        var recovered = store.Load<AppSettings>(path)!;
        Equal("first", recovered.CurrentProductId);
    });
}

static void TestConcurrentConfiguration()
{
    WithTemporaryDirectory(root =>
    {
        var image = Path.Combine(root, "source.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="));
        var models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);
        var portable = Path.Combine(root, "portable");
        var firstWorkspace = new WorkspaceService(new ConfigStore(), portable);
        var created = firstWorkspace.CreateProduct("协作产品", image, models);
        var summary = firstWorkspace.ListProducts().Single();
        var first = firstWorkspace.LoadProduct(summary)!;
        var secondWorkspace = new WorkspaceService(new ConfigStore(), portable);
        var second = secondWorkspace.LoadProduct(summary)!;

        first.Hotspots.Add(new PartHotspot { Name = "第一台电脑" });
        firstWorkspace.SaveProduct(first);
        second.Hotspots.Add(new PartHotspot { Name = "第二台电脑" });
        Throws<ProductConfigConflictException>(() => secondWorkspace.SaveProduct(second));

        var latest = firstWorkspace.LoadProduct(summary)!;
        True(latest.Revision > created.Revision);
        Equal(1, latest.Hotspots.Count);
        Equal("第一台电脑", latest.Hotspots.Single().Name);

        latest.FileBindings.Add(new FileBinding
        {
            RelativePath = "旧文件.3mf",
            GroupKey = "头盔",
            ManualOverride = true
        });
        firstWorkspace.SaveProduct(latest);
        var renameObserved = firstWorkspace.LoadProduct(summary)!;
        var remoteEdit = secondWorkspace.LoadProduct(summary)!;
        remoteEdit.Hotspots.Add(new PartHotspot { Name = "远程新增热点" });
        secondWorkspace.SaveProduct(remoteEdit);
        var merged = firstWorkspace.UpdateFileBindingAfterRename(renameObserved, "旧文件.3mf", "新文件.3mf");
        True(merged.Hotspots.Any(item => item.Name == "远程新增热点"));
        Equal("头盔", merged.FileBindings.Single(item => item.RelativePath == "新文件.3mf").GroupKey);

        var settingsPath = Path.Combine(portable, "settings.json");
        Parallel.For(0, 16, index =>
            new ConfigStore().Save(settingsPath, new AppSettings { CurrentProductId = index.ToString() }));
        True(new ConfigStore().Load<AppSettings>(settingsPath) is not null);
        Equal(0, Directory.EnumerateFiles(portable, "*.tmp", SearchOption.AllDirectories).Count());
    });
}

static void TestHotspotPresentation()
{
    Exception? threadFailure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var hotspot = new PartHotspot { Name = "头盔", GroupKeys = ["头盔"] };
            var control = new HotspotControl(hotspot);

            Equal(Cursors.Hand, control.Cursor);
            True(control.ToolTip is null);
            Equal(0, control.Children.OfType<TextBlock>().Count());

            control.EditToolTipText = "头盔 · 3 个文件";
            control.IsEditMode = true;
            Equal(Cursors.SizeAll, control.Cursor);
            Equal("头盔 · 3 个文件", control.ToolTip as string);
            Equal(0, control.Children.OfType<TextBlock>().Count());
        }
        catch (Exception exception)
        {
            threadFailure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (threadFailure is not null)
    {
        throw new InvalidOperationException("Hotspot UI state test failed.", threadFailure);
    }
}


static void TestNewerLegacyPortableMigration()
{
    WithTemporaryDirectory(root =>
    {
        var image = Path.Combine(root, "source.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="));
        var models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);
        var workspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "portable"));
        workspace.CreateProduct("产品", image, models);
        var summary = workspace.ListProducts().Single();
        var stale = workspace.LoadProduct(summary)!;
        var latest = workspace.LoadProduct(summary)!;
        latest.Hotspots.Add(new PartHotspot { Name = "共享较新热点" });
        workspace.SaveProduct(latest);

        var service = new PortableProductPackageService();
        var configDirectory = service.GetConfigDirectoryPath(models);
        var configPath = service.GetConfigPath(models);
        var legacyPath = Path.Combine(models, PortableProductPackageService.ConfigFileName);
        File.Move(configPath, legacyPath);
        Directory.Delete(configDirectory, true);

        var written = service.Save(stale, models, workspace.ResolvePortablePath(stale.DiagramPath));
        True(!written);
        True(File.Exists(configPath));
        True(File.Exists(Path.Combine(configDirectory, "包装图.png")));
        True(!File.Exists(legacyPath));
        var migrated = service.Load(models);
        Equal(latest.Revision, migrated.Product.Revision);
        True(migrated.Product.Hotspots.Any(item => item.Name == "共享较新热点"));
    });
}

static void TestGroupFiltering()
{
    var product = new ProductConfig
    {
        Name = "示例机器人",
        Hotspots =
        [
            new PartHotspot { Name = "头盔", GroupKeys = ["头盔"] }
        ]
    };
    var groups = new List<FileGroup>
    {
        MakeGroup("头盔"),
        MakeGroup("肩膀")
    };
    var service = new FileGroupFilterService();

    var pendingOnly = service.Apply(groups, product, false);
    Equal(1, pendingOnly.Count);
    Equal("肩膀", pendingOnly.Single().Key);
    True(groups.Single(group => group.Key == "头盔").IsMapped);
    True(!groups.Single(group => group.Key == "肩膀").IsMapped);

    var showAll = service.Apply(groups, product, true);
    Equal(2, showAll.Count);

    groups.Add(MakeGroup("新切片部件"));
    var afterExternalChange = service.Apply(groups, product, false);
    True(afterExternalChange.Any(group => group.Key == "新切片部件"));
}

static void TestRename()
{
    WithTemporaryDirectory(root =>
    {
        var original = Path.Combine(root, "示例机器人-头盔-10个.3mf");
        File.WriteAllBytes(original, []);
        var model = new ModelFile
        {
            FullPath = original,
            RelativePath = "示例机器人-头盔-10个.3mf",
            DisplayName = "示例机器人-头盔-10个.3mf",
            GroupKey = "头盔"
        };
        var service = new FileOperationsService();
        var renamed = service.Rename(model, "示例机器人-头盔-20个", root);
        Equal("示例机器人-头盔-20个.3mf", renamed);
        True(File.Exists(Path.Combine(root, renamed)));

        var renamedModel = new ModelFile
        {
            FullPath = Path.Combine(root, renamed),
            RelativePath = renamed,
            DisplayName = renamed,
            GroupKey = "头盔"
        };
        Throws<InvalidOperationException>(() => service.Rename(renamedModel, "CON", root));
        File.WriteAllBytes(Path.Combine(root, "already.3mf"), []);
        Throws<IOException>(() => service.Rename(renamedModel, "already.3mf", root));
    });
}

static void TestFileImport()
{
    WithTemporaryDirectory(root =>
    {
        var sourceRoot = Path.Combine(root, "incoming");
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(modelRoot);
        var source = Path.Combine(sourceRoot, "示例机器人-头盔-10个.3mf");
        var invalid = Path.Combine(sourceRoot, "说明.txt");
        var alreadyInside = Path.Combine(modelRoot, "已经在目录中.3mf");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllText(invalid, "not a model");
        File.WriteAllBytes(alreadyInside, []);
        var expectedUtc = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, expectedUtc);

        var service = new FileOperationsService();
        var result = service.CopyIntoModelRoot([source, invalid, alreadyInside], modelRoot);
        Equal(1, result.CopiedPaths.Count);
        Equal(2, result.Issues.Count);
        var destination = Path.Combine(modelRoot, Path.GetFileName(source));
        True(File.Exists(source));
        True(File.Exists(destination));
        Equal(expectedUtc, File.GetLastWriteTimeUtc(destination));
        Equal(0, Directory.EnumerateFiles(modelRoot, ".partmap-import-*.tmp").Count());

        var duplicate = service.CopyIntoModelRoot([source], modelRoot);
        Equal(0, duplicate.CopiedPaths.Count);
        Equal(1, duplicate.Issues.Count);

        var product = new ProductConfig { Name = "示例机器人" };
        var scanned = new FileGroupingService().Scan(product, modelRoot);
        var importedFile = scanned.Single(file => file.DisplayName == Path.GetFileName(source));
        Equal(expectedUtc, importedFile.LastModifiedLocal.ToUniversalTime());
        True(importedFile.LastModifiedText.Contains("最后修改：", StringComparison.Ordinal));

        var hotspot = new PartHotspot { Name = "头盔" };
        product.Hotspots.Add(hotspot);
        var boundGroups = new FileGroupingService().BindImportedFilesToHotspot(
            product, modelRoot, result.CopiedPaths, hotspot);
        Equal(1, boundGroups.Count);
        Equal("头盔", boundGroups.Single());
        True(hotspot.GroupKeys.Contains("头盔"));
        True(product.FileBindings.Any(binding =>
            binding.RelativePath == Path.GetFileName(source) && binding.GroupKey == "头盔"));
    });
}

static void TestDeleteSafety()
{
    WithTemporaryDirectory(root =>
    {
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var firstPath = Path.Combine(modelRoot, "头盔.3mf");
        var secondPath = Path.Combine(modelRoot, "肩膀.3mf");
        var outsidePath = Path.Combine(root, "outside.3mf");
        File.WriteAllBytes(firstPath, []);
        File.WriteAllBytes(secondPath, []);
        File.WriteAllBytes(outsidePath, []);

        var recycled = new List<string>();
        var service = new FileOperationsService(path =>
        {
            recycled.Add(path);
            File.Delete(path);
        });
        var selected = new[]
        {
            MakeModelFile(firstPath, modelRoot, "头盔"),
            MakeModelFile(secondPath, modelRoot, "肩膀")
        };

        var deleted = service.DeleteToRecycleBin(selected, modelRoot);
        Equal(2, deleted.Count);
        Equal(2, recycled.Count);
        True(!File.Exists(firstPath));
        True(!File.Exists(secondPath));

        var outsideModel = MakeModelFile(outsidePath, modelRoot, "越界");
        Throws<UnauthorizedAccessException>(() => service.DeleteToRecycleBin([outsideModel], modelRoot));
        True(File.Exists(outsidePath));
    });
}

static void TestArchiveToHistory()
{
    WithTemporaryDirectory(root =>
    {
        var modelRoot = Path.Combine(root, "models");
        var historyRoot = Path.Combine(modelRoot, FileGroupingService.HistoryDirectoryName);
        var gcodeRoot = Path.Combine(modelRoot, FileGroupingService.GcodeDirectoryName);
        var gcodeHistoryRoot = Path.Combine(gcodeRoot, FileGroupingService.HistoryDirectoryName);
        Directory.CreateDirectory(historyRoot);
        Directory.CreateDirectory(gcodeRoot);
        var standardPath = Path.Combine(modelRoot, "头盔.3mf");
        var gcodePath = Path.Combine(gcodeRoot, "头盔切片-v1_1.5h_20g.gcode.3mf");
        var outsidePath = Path.Combine(root, "外部.3mf");
        File.WriteAllBytes(standardPath, [1]);
        File.WriteAllBytes(gcodePath, [2]);
        File.WriteAllBytes(outsidePath, [3]);
        File.WriteAllBytes(Path.Combine(historyRoot, "头盔_2026-08-27.3mf"), [4]);

        var service = new FileOperationsService();
        var result = service.ArchiveToHistory(
            [
                MakeModelFile(standardPath, modelRoot, "头盔"),
                MakeModelFile(gcodePath, modelRoot, "头盔切片"),
                MakeModelFile(outsidePath, modelRoot, "外部")
            ],
            modelRoot,
            new DateTime(2026, 8, 27));

        Equal(1, result.ArchivedPaths.Count);
        Equal(2, result.Issues.Count);
        True(File.Exists(Path.Combine(historyRoot, "头盔_2026-08-27_v1.3mf")));
        True(!Directory.Exists(gcodeHistoryRoot) || !Directory.EnumerateFiles(gcodeHistoryRoot, "*.gcode.3mf").Any());
        True(!File.Exists(standardPath));
        True(File.Exists(gcodePath));
        True(File.Exists(outsidePath));

        File.WriteAllBytes(standardPath, [5]);
        var second = service.ArchiveToHistory(
            [MakeModelFile(standardPath, modelRoot, "头盔")], modelRoot, new DateTime(2026, 8, 27));
        Equal(1, second.ArchivedPaths.Count);
        True(File.Exists(Path.Combine(historyRoot, "头盔_2026-08-27_v2.3mf")));

        var product = new ProductConfig { Name = "产品", IncludeSubdirectories = true };
        Equal(1, new FileGroupingService().Scan(product, modelRoot).Count);
        True(FileGroupingService.IsHistoryPath("历史/头盔_2026-08-27_v1.3mf"));
        True(FileGroupingService.IsHistoryPath("gcode/历史/头盔-v1_1.5h_20g.gcode.3mf"));
        True(!FileGroupingService.IsHistoryPath("批次A/历史/头盔.3mf"));
    });
}

static void TestSliceSummaryRounding()
{
    WithTemporaryDirectory(root =>
    {
        var source = Path.Combine(root, "source.3mf");
        var fakeExe = Path.Combine(root, "bambu-studio.exe");
        File.WriteAllText(source, "source");
        File.WriteAllText(fakeExe, "fake");

        var firstOutput = Path.Combine(root, "first.gcode.3mf");
        var first = new BambuStudioSlicingService(
            new FakeSlicerRunner("2h 10m", 10.1), () => fakeExe, TimeSpan.FromSeconds(5))
            .SliceAsync(source, firstOutput).GetAwaiter().GetResult();
        True(first.Succeeded);
        Equal("2.5h", first.Summary!.TimeLabel);
        Equal("11g", first.Summary.WeightLabel);

        var secondOutput = Path.Combine(root, "second.gcode.3mf");
        var second = new BambuStudioSlicingService(
            new FakeSlicerRunner("2h 19m", 10.0), () => fakeExe, TimeSpan.FromSeconds(5))
            .SliceAsync(source, secondOutput).GetAwaiter().GetResult();
        True(second.Succeeded);
        Equal("3h", second.Summary!.TimeLabel);
        Equal("10g", second.Summary.WeightLabel);

        // 打印时间显示采用保守的半小时取整规则：
        // 先向上到最近 30 分钟点；与该点差值 ≤ 12 分钟时再加 30 分钟（含差值 0）。
        var boundaryCases = new (string Raw, string Expected)[]
        {
            ("2h 17m 59s", "2.5h"),
            ("2h 18m", "3h"),
            ("2h 30m", "3h"),
            ("2h 47m 59s", "3h"),
            ("2h 48m", "3.5h"),
            ("3h 0m", "3.5h"),
            ("0h 26m 34s", "1h"),
        };
        var index = 0;
        foreach (var (raw, expected) in boundaryCases)
        {
            index++;
            var output = Path.Combine(root, $"boundary-{index}.gcode.3mf");
            var sliced = new BambuStudioSlicingService(
                new FakeSlicerRunner(raw, 10.0), () => fakeExe, TimeSpan.FromSeconds(5))
                .SliceAsync(source, output).GetAwaiter().GetResult();
            True(sliced.Succeeded);
            Equal(expected, sliced.Summary!.TimeLabel);
        }
    });
}

static void TestBambuStudioSelection()
{
    WithTemporaryDirectory(root =>
    {
        var running = Path.Combine(root, "running-bambu-studio.exe");
        var installed = Path.Combine(root, "installed-bambu-studio.exe");
        File.WriteAllText(running, "running");
        File.WriteAllText(installed, "installed");
        Equal(running, BambuStudioSlicingService.SelectPreferredExecutable([running], [installed]));
    });
}

static void TestOneClickSlicing()
{
    WithTemporaryDirectory(root =>
    {
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var source = Path.Combine(modelRoot, "产品-肩膀-10对-P1S.3mf");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using var model = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open());
            model.Write("<model/>");
        }
        var fakeExe = Path.Combine(root, "bambu-studio.exe");
        File.WriteAllText(fakeExe, "fake");
        var runner = new FakeSlicerRunner("1h 46m 57s", 28.79);
        var arguments = BambuStudioSlicingService.BuildArguments(source, Path.Combine(root, "probe.gcode.3mf"));
        True(arguments.Contains("--min-save"));
        var slicer = new BambuStudioSlicingService(runner, () => fakeExe, TimeSpan.FromSeconds(5));
        var service = new OneClickSlicingService(slicer, new FileOperationsService());

        var projectFormat = Path.Combine(root, "project-format.gcode.3mf");
        using (var archive = ZipFile.Open(projectFormat, ZipArchiveMode.Create))
        {
            using (var modelWriter = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open())) modelWriter.Write("<model/>");
            using (var objectWriter = new StreamWriter(archive.CreateEntry("3D/Objects/object_1.model").Open())) objectWriter.Write("<object/>");
            using var gcodeWriter = new StreamWriter(archive.CreateEntry("Metadata/plate_1.gcode").Open());
            gcodeWriter.WriteLine("; total estimated time: 1h");
            gcodeWriter.WriteLine("; total filament weight [g] : 10");
        }
        True(!BambuStudioSlicingService.ValidateAndReadSummary(projectFormat, out _, out var projectError));
        Equal("输出包含源模型几何，不是纯 G-code 3MF。", projectError);

        var first = service.GenerateAsync(source, modelRoot).GetAwaiter().GetResult();
        True(first.Succeeded);
        Equal("产品-肩膀-10对-P1S-v1_2h_29g.gcode.3mf", Path.GetFileName(first.TargetPath));
        True(File.Exists(first.TargetPath!));

        var second = service.GenerateAsync(source, modelRoot).GetAwaiter().GetResult();
        True(second.Succeeded);
        Equal("产品-肩膀-10对-P1S-v2_2h_29g.gcode.3mf", Path.GetFileName(second.TargetPath));
        Equal(1, second.ArchivedPaths.Count);
        True(!File.Exists(Path.Combine(modelRoot, FileGroupingService.GcodeDirectoryName,
            FileGroupingService.HistoryDirectoryName, "产品-肩膀-10对-P1S-v1_2h_29g.gcode.3mf")));
        var renamed = Path.Combine(modelRoot, "产品-肩膀-20对-P2S.3mf");
        File.Move(source, renamed);
        var afterRename = service.GenerateAsync(renamed, modelRoot).GetAwaiter().GetResult();
        True(afterRename.Succeeded);
        Equal("产品-肩膀-20对-P2S-v1_2h_29g.gcode.3mf", Path.GetFileName(afterRename.TargetPath));

        var product = new ProductConfig { Name = "产品", IncludeSubdirectories = true };
        var scan = new FileGroupingService().Scan(product, modelRoot);
        Equal(3, scan.Count);
        True(scan.All(file => !FileGroupingService.IsHistoryPath(file.RelativePath)));
        Equal(2, scan.Count(file => FileGroupingService.IsGcode3mf(file.FullPath)));
    });
}

static void TestMultiMaterialWeightSum()
{
    WithTemporaryDirectory(root =>
    {
        var path = Path.Combine(root, "multi.gcode.3mf");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var model = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open())) model.Write("<model/>");
            using var gcode = new StreamWriter(archive.CreateEntry("Metadata/plate_1.gcode").Open());
            gcode.WriteLine("; total estimated time: 1h 10m");
            gcode.WriteLine("; total filament weight [g] : 81.15,20.28,31.17");
        }

        var ok = BambuStudioSlicingService.ValidateAndReadSummary(path, out var summary, out _);
        True(ok);
        Equal("133g", summary.WeightLabel);
    });
}
static void TestPlateMetadataRead()
{
    WithTemporaryDirectory(root =>
    {
        var path = Path.Combine(root, "plates.3mf");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Metadata/model_settings.config").Open());
            writer.Write("<config><plate><metadata key=\"plater_id\" value=\"1\"/><metadata key=\"plater_name\" value=\"手护板\"/></plate><plate><metadata key=\"plater_id\" value=\"2\"/><metadata key=\"plater_name\" value=\"纹理板\"/></plate></config>");
        }

        var type = typeof(BambuStudioSlicingService).Assembly.GetType("PartMap.Services.ThreeMfMetadataService");
        True(type is not null);
        var method = type!.GetMethod("ReadPlates");
        True(method is not null);
        var items = ((System.Collections.IEnumerable)method!.Invoke(Activator.CreateInstance(type), [path])!).Cast<object>().ToList();
        Equal(2, items.Count);
        Equal(1, (int)items[0].GetType().GetProperty("PlateId")!.GetValue(items[0])!);
        Equal("手护板", (string)items[0].GetType().GetProperty("Name")!.GetValue(items[0])!);
        Equal("纹理板", (string)items[1].GetType().GetProperty("Name")!.GetValue(items[1])!);
    });
}
static void TestPlateSpecificSliceArguments()
{
    var method = typeof(BambuStudioSlicingService).GetMethod("BuildArguments",
        [typeof(string), typeof(string), typeof(int)]);
    True(method is not null);
    var args = (IReadOnlyList<string>)method!.Invoke(null, ["source.3mf", "output.gcode.3mf", 2])!;
    var sliceIndex = args.ToList().IndexOf("--slice");
    True(sliceIndex >= 0 && sliceIndex + 1 < args.Count);
    Equal("2", args[sliceIndex + 1]);
}
static void TestSliceParameterHistory()
{
    WithTemporaryDirectory(root =>
    {
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var source = Path.Combine(modelRoot, "产品-肩膀-10对.3mf");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using (var model = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open())) model.Write("<model/>");
            using (var project = new StreamWriter(archive.CreateEntry("Metadata/project_settings.config").Open()))
                project.Write("{\"layer_height\":\"0.2\",\"seam_position\":\"aligned\"}");
            using (var settings = new StreamWriter(archive.CreateEntry("Metadata/model_settings.config").Open()))
                settings.Write("<config><plate><metadata key=\"plater_id\" value=\"1\"/><metadata key=\"plater_name\" value=\"手护板\"/></plate></config>");
        }
        var fakeExe = Path.Combine(root, "bambu-studio.exe");
        File.WriteAllText(fakeExe, "fake");
        var slicer = new BambuStudioSlicingService(new FakeSlicerRunner("1h", 10.1), () => fakeExe, TimeSpan.FromSeconds(5));
        var service = new OneClickSlicingService(slicer, new FileOperationsService());

        True(service.GenerateAsync(source, modelRoot).GetAwaiter().GetResult().Succeeded);
        var second = service.GenerateAsync(source, modelRoot).GetAwaiter().GetResult();
        True(second.Succeeded);
        Equal("产品-肩膀-10对-v2_1.5h_11g.gcode.3mf", Path.GetFileName(second.TargetPath));

        var gcodeRoot = Path.Combine(modelRoot, FileGroupingService.GcodeDirectoryName);
        var history = Path.Combine(gcodeRoot, FileGroupingService.HistoryDirectoryName);
        Equal(1, Directory.EnumerateFiles(gcodeRoot, "*.gcode.3mf", SearchOption.TopDirectoryOnly).Count());
        Equal(0, Directory.EnumerateFiles(history, "*.gcode.3mf", SearchOption.TopDirectoryOnly).Count());
        var records = Directory.EnumerateFiles(history, "*.params.json", SearchOption.TopDirectoryOnly).OrderBy(path => path).ToList();
        Equal(2, records.Count);
        var text = File.ReadAllText(records[0]);
        True(text.Contains("project_settings.config", StringComparison.Ordinal));
        True(text.Contains("layer_height", StringComparison.Ordinal));
        True(text.Contains("model_settings.config", StringComparison.Ordinal));
        True(text.Contains("plater_name", StringComparison.Ordinal));
    });
}
static void TestNamedPlateSeries()
{
    WithTemporaryDirectory(root =>
    {
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var source = Path.Combine(modelRoot, "产品-护甲.3mf");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using var model = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open());
            model.Write("<model/>");
        }
        var sliced = Path.Combine(root, "slice.gcode.3mf");
        using (var archive = ZipFile.Open(sliced, ZipArchiveMode.Create))
        {
            using (var model = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open())) model.Write("<model/>");
            using var gcode = new StreamWriter(archive.CreateEntry("Metadata/plate_1.gcode").Open());
            gcode.WriteLine("; total estimated time: 1h");
            gcode.WriteLine("; total filament weight [g] : 10");
        }
        var files = new FileOperationsService();
        var summary = new BambuSlicePrintSummary("1h", "10g");
        var a1 = files.FinalizeSlicedGcode(source, sliced, modelRoot, summary, 1, "手护板");
        Equal("产品-护甲-手护板-v1_1h_10g.gcode.3mf", Path.GetFileName(a1.TargetPath));
        var b1 = files.FinalizeSlicedGcode(source, sliced, modelRoot, summary, 2, "纹理板");
        Equal("产品-护甲-纹理板-v1_1h_10g.gcode.3mf", Path.GetFileName(b1.TargetPath));
        var a2 = files.FinalizeSlicedGcode(source, sliced, modelRoot, summary, 1, "手护板");
        Equal("产品-护甲-手护板-v2_1h_10g.gcode.3mf", Path.GetFileName(a2.TargetPath));
        var current = Directory.EnumerateFiles(Path.Combine(modelRoot, "gcode"), "*.gcode.3mf").Select(Path.GetFileName).OrderBy(x => x).ToList();
        Equal(2, current.Count);
        True(current.Contains("产品-护甲-手护板-v2_1h_10g.gcode.3mf"));
        True(current.Contains("产品-护甲-纹理板-v1_1h_10g.gcode.3mf"));
    });
}

static void TestOneClickNamedPlate()
{
    WithTemporaryDirectory(root =>
    {
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var source = Path.Combine(modelRoot, "产品-护甲.3mf");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using var model = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open());
            model.Write("<model/>");
        }
        var fakeExe = Path.Combine(root, "bambu-studio.exe");
        File.WriteAllText(fakeExe, "fake");
        var runner = new FakeSlicerRunner("1h", 10);
        var service = new OneClickSlicingService(
            new BambuStudioSlicingService(runner, () => fakeExe, TimeSpan.FromSeconds(5)),
            new FileOperationsService());
        var method = typeof(OneClickSlicingService).GetMethod("GenerateAsync",
            [typeof(string), typeof(string), typeof(int), typeof(string), typeof(CancellationToken)]);
        True(method is not null);
        var task = (Task<OneClickSliceResult>)method!.Invoke(service,
            [source, modelRoot, 2, "纹理板", CancellationToken.None])!;
        var result = task.GetAwaiter().GetResult();
        True(result.Succeeded);
        Equal("产品-护甲-纹理板-v1_1.5h_10g.gcode.3mf", Path.GetFileName(result.TargetPath));
        var lastArguments = runner.LastArguments ?? throw new InvalidOperationException("missing slicer arguments");
        var sliceIndex = lastArguments.ToList().IndexOf("--slice");
        Equal("2", lastArguments[sliceIndex + 1]);
    });
}

static void TestGcodeOnlyRestore()
{
    WithTemporaryDirectory(root =>
    {
        var image = Path.Combine(root, "source.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zt9sAAAAASUVORK5CYII="));
        var modelRoot = Path.Combine(root, "models");
        Directory.CreateDirectory(modelRoot);
        var source3mf = Path.Combine(modelRoot, "豚巴达-战剑-剑鞘-100个.3mf");
        File.WriteAllBytes(source3mf, [1]);

        var firstWorkspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "first-PartMap"));
        var product = firstWorkspace.CreateProduct("豚巴达", image, modelRoot);
        var grouping = new FileGroupingService();
        Equal("战剑-剑鞘", grouping.Scan(product, modelRoot).Single().GroupKey);
        product.Hotspots.Add(new PartHotspot { Name = "剑鞘", GroupKeys = ["战剑-剑鞘"] });
        firstWorkspace.SaveProduct(product);

        var gcodeRoot = Path.Combine(modelRoot, FileGroupingService.GcodeDirectoryName);
        var gcodeHistory = Path.Combine(gcodeRoot, FileGroupingService.HistoryDirectoryName);
        Directory.CreateDirectory(gcodeHistory);
        var activeGcode = Path.Combine(gcodeRoot,
            "豚巴达-战剑-剑鞘-100个_4.5h_53g.gcode.3mf");
        File.WriteAllBytes(activeGcode, [2]);
        File.WriteAllBytes(Path.Combine(gcodeHistory,
            "豚巴达-战剑-剑鞘-100个_4h_50g.gcode.3mf"), [3]);
        Directory.CreateDirectory(Path.Combine(modelRoot, FileGroupingService.HistoryDirectoryName));
        File.WriteAllBytes(Path.Combine(modelRoot, FileGroupingService.HistoryDirectoryName,
            "豚巴达-战剑-剑鞘-80个.3mf"), [4]);
        File.Delete(source3mf);

        var restoredWorkspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "second-PartMap"));
        var restored = restoredWorkspace.ImportProductFolder(modelRoot);
        True(!restored.IncludeSubdirectories);
        var restoredFiles = grouping.Scan(restored, modelRoot);

        Equal(1, restoredFiles.Count);
        var restoredGcode = restoredFiles.Single();
        Equal("战剑-剑鞘", restoredGcode.GroupKey);
        Equal("gcode/豚巴达-战剑-剑鞘-100个_4.5h_53g.gcode.3mf", restoredGcode.RelativePath);
        var hotspot = restored.Hotspots.Single(item => item.Name == "剑鞘");
        True(hotspot.GroupKeys.Contains(restoredGcode.GroupKey));
        True(restored.FileBindings.Any(binding =>
            binding.RelativePath == restoredGcode.RelativePath && binding.GroupKey == "战剑-剑鞘"));
    });
}

static void TestTransientPortableConfigRetry()
{
    WithTemporaryDirectory(root =>
    {
        var models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);
        var diagram = Path.Combine(root, "diagram.png");
        File.WriteAllBytes(diagram, [1, 2, 3, 4]);
        var workspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "app"));
        var product = workspace.CreateProduct("产品A", diagram, models);
        var portable = new PortableProductPackageService();
        var configPath = portable.GetConfigPath(models);

        var blocker = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(250);
            blocker.Dispose();
        });

        product.Name = "产品B";
        try
        {
            workspace.SaveProduct(product);
        }
        finally
        {
            release.GetAwaiter().GetResult();
            blocker.Dispose();
        }
        Equal("产品B", portable.Load(models).Product.Name);
    });
}

static void TestThumbnailReadWhileWritable()
{
    WithTemporaryDirectory(root =>
    {
        var path = Path.Combine(root, "shared.3mf");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            using var stream = archive.CreateEntry("Metadata/plate_1.png").Open();
            stream.Write(png);
        }

        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        var thumbnail = new ThumbnailService().LoadAsync(path).GetAwaiter().GetResult();
        True(thumbnail is not null);
    });
}

static void TestBatchSlicingQueue()
{
    var active = 0;
    var maxActive = 0;
    var order = new List<string>();
    var service = new BatchSlicingService(async (source, _, cancellationToken) =>
    {
        active++;
        maxActive = Math.Max(maxActive, active);
        order.Add(Path.GetFileName(source));
        await Task.Delay(20, cancellationToken);
        active--;
        return source.Contains("失败", StringComparison.Ordinal)
            ? new OneClickSliceResult { Succeeded = false, Message = "测试失败" }
            : new OneClickSliceResult { Succeeded = true, TargetPath = source + ".gcode.3mf", Message = "完成" };
    });
    var requests = new[]
    {
        new BatchSliceRequest("A.3mf", "A.3mf", "A"),
        new BatchSliceRequest("失败.3mf", "失败.3mf", "失败"),
        new BatchSliceRequest("C.3mf", "C.3mf", "C")
    };
    var progress = new List<BatchSliceProgress>();
    var results = service.RunAsync(requests, "models", new Progress<BatchSliceProgress>(item => progress.Add(item)))
        .GetAwaiter().GetResult();

    Equal(1, maxActive);
    Equal("A.3mf|失败.3mf|C.3mf", string.Join('|', order));
    Equal(3, results.Count);
    Equal(BatchSliceState.Completed, results[0].State);
    Equal(BatchSliceState.Failed, results[1].State);
    Equal(BatchSliceState.Completed, results[2].State);
}

static void TestBatchBindingUpdate()
{
    WithTemporaryDirectory(root =>
    {
        var models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);
        var diagram = Path.Combine(root, "diagram.png");
        File.WriteAllBytes(diagram, [1, 2, 3]);
        var workspace = new WorkspaceService(new ConfigStore(), Path.Combine(root, "app"));
        var product = workspace.CreateProduct("产品", diagram, models);
        var beforeRevision = product.Revision;
        var updated = workspace.UpsertFileBindings(product,
        [
            ("gcode/A-v1_1h_10g.gcode.3mf", "A"),
            ("gcode/B-v1_2h_20g.gcode.3mf", "B")
        ]);

        Equal(beforeRevision + 1, updated.Revision);
        True(updated.FileBindings.Any(item => item.RelativePath == "gcode/A-v1_1h_10g.gcode.3mf" && item.GroupKey == "A"));
        True(updated.FileBindings.Any(item => item.RelativePath == "gcode/B-v1_2h_20g.gcode.3mf" && item.GroupKey == "B"));
    });
}

static void TestNetworkPathDetection()
{
    True(FileOperationsService.IsNetworkPath(@"\\server\share\models"));
    True(!FileOperationsService.IsNetworkPath(Path.GetTempPath()));
    Equal(@"\\server\share\models", NetworkPathService.ToStablePath(@"\\server\share\models"));
}

static void WithTemporaryDirectory(Action<string> action)
{
    var root = Path.Combine(Path.GetTempPath(), "PartMap.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        action(root);
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static FileGroup MakeGroup(string key)
{
    return new FileGroup
    {
        Key = key,
        Files =
        [
            new ModelFile
            {
                FullPath = key + ".3mf",
                RelativePath = key + ".3mf",
                DisplayName = key + ".3mf",
                GroupKey = key
            }
        ]
    };
}

static ModelFile MakeModelFile(string fullPath, string modelRoot, string groupKey)
{
    return new ModelFile
    {
        FullPath = fullPath,
        RelativePath = FileGroupingService.NormalizeRelative(Path.GetRelativePath(modelRoot, fullPath)),
        DisplayName = Path.GetFileName(fullPath),
        GroupKey = groupKey
    };
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected: {expected}; actual: {actual}");
    }
}

static void True(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Expected condition to be true.");
    }
}

static void Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
}

sealed class FakeSlicerRunner(string timeText, double grams) : ISlicerProcessRunner
{
    public IReadOnlyList<string>? LastArguments { get; private set; }

    public Task<SlicerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        LastArguments = arguments.ToList();
        var index = arguments.ToList().IndexOf("--export-3mf");
        if (index < 0 || index + 1 >= arguments.Count) throw new InvalidOperationException("missing output");
        var output = arguments[index + 1];
        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("3D/3dmodel.model").Open()))
            writer.Write("<model/>");
        using (var writer = new StreamWriter(archive.CreateEntry("Metadata/plate_1.gcode").Open()))
        {
            writer.WriteLine($"; total estimated time: {timeText}");
            writer.WriteLine($"; total filament weight [g] : {grams.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return Task.FromResult(new SlicerProcessResult(0, "", ""));
    }
}
