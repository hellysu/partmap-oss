using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PartMap.Controls;
using PartMap.Dialogs;
using PartMap.Models;
using PartMap.Services;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace PartMap;

public partial class MainWindow : Window
{
    private const string GroupDragFormat = "PartMap.GroupKey";
    private const string InternalFileDragFormat = "PartMap.InternalFileDrag";
    private const string InternalFileGroupKeysFormat = "PartMap.FileGroupKeys";
    private readonly ConfigStore _configStore = new();
    private readonly FileGroupingService _groupingService = new();
    private readonly FileGroupFilterService _groupFilterService = new();
    private readonly FileOperationsService _fileOperations = new();
    private readonly OneClickSlicingService _oneClickSlicingService = new();
    private readonly BatchSlicingService _batchSlicingService;
    private readonly ThumbnailService _thumbnailService = new();
    private readonly ThreeMfMetadataService _threeMfMetadataService = new();
    private readonly WorkspaceService _workspace;
    private readonly DispatcherTimer _watcherDebounce;
    private readonly DispatcherTimer _configWatcherDebounce;
    private readonly DispatcherTimer _configPollTimer;

    private ProductConfig? _product;
    private List<ModelFile> _allFiles = [];
    private List<ModelFile> _files = [];
    private List<FileGroup> _groups = [];
    private PartHotspot? _selectedHotspot;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _configWatcher;
    private bool _suppressProductSelection;
    private bool _suppressIncludeSubdirectoriesChange;
    private BatchSlicingWindow? _batchSlicingWindow;
    private bool _slicingBusy;
    private bool _batchSlicingActive;

    private Point _groupDragStart;
    private Point _fileDragStart;
    private bool _drawMode;
    private Point _drawStart;
    private Rectangle? _drawPreview;
    private PartHotspot? _movingHotspot;
    private Point _moveStart;
    private double _moveOriginalX;
    private double _moveOriginalY;
    private HotspotControl? _externalDropTarget;
    private bool _externalDropOverFilesPanel;

    public MainWindow()
    {
        InitializeComponent();
        UpdateFileSectionVisibility();
        _workspace = new WorkspaceService(_configStore);
        _batchSlicingService = new BatchSlicingService(_oneClickSlicingService);
        OpenModelsButton.ToolTip = "打开当前产品直接绑定的原模型目录";
        ChangeModelsButton.ToolTip = "重新选择原模型目录；不会移动或复制文件";
        _watcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _watcherDebounce.Tick += (_, _) =>
        {
            _watcherDebounce.Stop();
            ReloadFiles("检测到模型目录变化，列表已刷新。");
        };
        _configWatcherDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _configWatcherDebounce.Tick += (_, _) =>
        {
            _configWatcherDebounce.Stop();
            ReloadSharedProductConfiguration();
        };
        _configPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _configPollTimer.Tick += (_, _) => ReloadSharedProductConfiguration();

        UpdateModeLayout();

        Loaded += (_, _) => LoadProductList();
        Closed += (_, _) =>
        {
            DisposeProductWatchers();
            _batchSlicingWindow?.ForceClose();
        };
    }

    private bool IsEditMode => EditModeToggle.IsChecked == true;

    private void LoadProductList(string? preferredProductId = null)
    {
        var products = _workspace.ListProducts();
        var settings = _workspace.LoadSettings();
        var targetId = preferredProductId ?? settings.CurrentProductId;

        _suppressProductSelection = true;
        ProductPicker.ItemsSource = products;
        ProductPicker.SelectedItem = products.FirstOrDefault(item => item.Id == targetId) ?? products.FirstOrDefault();
        _suppressProductSelection = false;

        if (ProductPicker.SelectedItem is ProductSummary selected)
        {
            LoadProduct(selected);
        }
        else
        {
            ShowEmptyState();
        }
    }

    private void ProductPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProductSelection || ProductPicker.SelectedItem is not ProductSummary summary)
        {
            return;
        }

        LoadProduct(summary);
    }

    private void LoadProduct(ProductSummary summary)
    {
        try
        {
            DisposeProductWatchers();
            _product = _workspace.LoadProduct(summary);
            if (_product is null)
            {
                throw new InvalidDataException("产品配置为空或无法读取。");
            }

            _workspace.SaveSettings(new AppSettings { CurrentProductId = _product.Id });
            OpenModelsButton.ToolTip = $"打开原模型目录\n{_workspace.ResolvePortablePath(_product.ModelRoot)}";
            UpdateProductOptions();
            UpdateModeLayout();
            LoadDiagram();
            _selectedHotspot = null;
            WelcomePanel.Visibility = Visibility.Collapsed;
            StartConfigWatcher();
            if (IsModelDirectoryAvailable())
            {
                ReloadFiles($"已打开产品：{_product.Name}", true);
                StartWatcher();
            }
            else
            {
                ShowModelDirectoryUnavailable(true);
            }
        }
        catch (Exception exception)
        {
            ShowError("无法打开产品", exception);
            ShowEmptyState();
        }
    }

    private void LoadDiagram()
    {
        if (_product is null)
        {
            return;
        }

        var path = _workspace.ResolvePortablePath(_product.DiagramPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到产品包装图。", path);
        }

        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        DiagramImage.Source = bitmap;
        DiagramSurface.Width = Math.Max(1, bitmap.PixelWidth);
        DiagramSurface.Height = Math.Max(1, bitmap.PixelHeight);

        Dispatcher.BeginInvoke(() =>
        {
            var availableWidth = Math.Max(320, DiagramScroll.ViewportWidth - 24);
            var availableHeight = Math.Max(240, DiagramScroll.ViewportHeight - 24);
            var fit = Math.Min(availableWidth / bitmap.PixelWidth, availableHeight / bitmap.PixelHeight);
            ZoomSlider.Value = Math.Clamp(fit, ZoomSlider.Minimum, 1.0);
        }, DispatcherPriority.Loaded);
    }

    private void ReloadFiles(string? statusMessage = null, bool openUnmappedPanel = false)
    {
        if (_product is null)
        {
            return;
        }

        if (!IsModelDirectoryAvailable())
        {
            ShowModelDirectoryUnavailable(false);
            return;
        }

        try
        {
            var selectedGroup = (GroupsList.SelectedItem as FileGroup)?.Key;
            var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
            _allFiles = _groupingService.Scan(_product, modelRoot).ToList();
            ApplyFileTypeFilter();
            _groups = _files
                .GroupBy(file => file.GroupKey, StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new FileGroup
                {
                    Key = group.Key,
                    Files = group.OrderBy(file => file.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
                })
                .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var mapped = _product.Hotspots.SelectMany(hotspot => hotspot.GroupKeys)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var unmappedCount = _groups.Count(group => !mapped.Contains(group.Key));
            if (openUnmappedPanel && unmappedCount > 0)
            {
                ShowAllGroupsToggle.IsChecked = false;
                EditModeToggle.IsChecked = true;
                UpdateModeLayout();
            }

            RefreshGroupList(selectedGroup);
            RenderHotspots();
            RefreshDetails();
            StatusText.Text = statusMessage ??
                $"{_files.Count} 个模型，{_groups.Count} 个文件组，{unmappedCount} 个文件组尚未放到图片。";
            if (openUnmappedPanel && unmappedCount > 0)
            {
                StatusText.Text = $"发现 {unmappedCount} 个待归类文件组，已自动打开左侧栏。";
            }


        }
        catch (Exception exception)
        {
            ShowError("刷新模型失败", exception);
        }
    }

    private ModelFileDisplayFilter SelectedFileTypeFilter => FileTypeFilter.SelectedIndex switch
    {
        1 => ModelFileDisplayFilter.Gcode3mf,
        2 => ModelFileDisplayFilter.Standard3mf,
        _ => ModelFileDisplayFilter.All
    };

    private void ApplyFileTypeFilter()
    {
        var filter = SelectedFileTypeFilter;
        _files = _allFiles
            .Where(file => FileGroupingService.MatchesDisplayFilter(file.FullPath, filter))
            .ToList();
    }

    private void FileTypeFilter_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFileSectionVisibility();
        if (_product is null)
        {
            return;
        }

        var selectedGroup = (GroupsList.SelectedItem as FileGroup)?.Key;
        ApplyFileTypeFilter();
        _groups = _files
            .GroupBy(file => file.GroupKey, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new FileGroup
            {
                Key = group.Key,
                Files = group.OrderBy(file => file.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
            })
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RefreshGroupList(selectedGroup);
        RenderHotspots();
        RefreshDetails();
        var filterName = SelectedFileTypeFilter switch
        {
            ModelFileDisplayFilter.Gcode3mf => ".gcode.3mf",
            ModelFileDisplayFilter.Standard3mf => "普通 .3mf",
            _ => "全部 3MF"
        };
        StatusText.Text = $"当前筛选：{filterName}，显示 {_files.Count}/{_allFiles.Count} 个文件；文件和映射未修改。";
    }

    private void UpdateFileSectionVisibility()
    {
        if (StandardFilesSectionRow is null || FileSectionsSeparatorRow is null || GcodeFilesSectionRow is null)
        {
            return;
        }

        var showStandard = SelectedFileTypeFilter != ModelFileDisplayFilter.Gcode3mf;
        var showGcode = SelectedFileTypeFilter != ModelFileDisplayFilter.Standard3mf;
        StandardFilesSectionRow.Height = showStandard ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FileSectionsSeparatorRow.Height = showStandard && showGcode ? new GridLength(8) : new GridLength(0);
        GcodeFilesSectionRow.Height = showGcode ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void RefreshGroupList(string? preferredGroupKey = null)
    {
        if (_product is null)
        {
            GroupsList.ItemsSource = null;
            UnmappedGroupCountText.Text = "0 个文件组";
            EmptyGroupsText.Visibility = Visibility.Visible;
            return;
        }

        preferredGroupKey ??= (GroupsList.SelectedItem as FileGroup)?.Key;
        var visibleGroups = _groupFilterService.Apply(
            _groups,
            _product,
            ShowAllGroupsToggle.IsChecked == true);
        var unmappedCount = _groups.Count(group => !group.IsMapped);

        GroupsList.ItemsSource = visibleGroups;
        GroupsList.SelectedItem = visibleGroups.FirstOrDefault(group =>
            string.Equals(group.Key, preferredGroupKey, StringComparison.CurrentCultureIgnoreCase));
        UnmappedGroupCountText.Text = $"{unmappedCount} 个待归类 · {_groups.Count} 个文件组";
        EmptyGroupsText.Text = ShowAllGroupsToggle.IsChecked == true ? "暂无模型文件" : "所有文件都已归类";
        EmptyGroupsText.Visibility = visibleGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowAllGroupsToggle.Content = ShowAllGroupsToggle.IsChecked == true ? "仅看待归类" : "显示全部";
    }

    private void RenderHotspots()
    {
        _externalDropTarget = null;
        _externalDropOverFilesPanel = false;
        HotspotCanvas.Children.Clear();
        if (_product is null || DiagramSurface.Width <= 0 || DiagramSurface.Height <= 0)
        {
            return;
        }

        foreach (var hotspot in _product.Hotspots)
        {
            var card = CreateHotspotCard(hotspot);
            HotspotCanvas.Children.Add(card);
            Canvas.SetLeft(card, hotspot.X * DiagramSurface.Width);
            Canvas.SetTop(card, hotspot.Y * DiagramSurface.Height);
        }
    }

    private HotspotControl CreateHotspotCard(PartHotspot hotspot)
    {
        var fileCount = _files.Count(file => hotspot.GroupKeys.Contains(file.GroupKey, StringComparer.CurrentCultureIgnoreCase));
        var selected = _selectedHotspot?.Id == hotspot.Id;
        var card = new HotspotControl(hotspot)
        {
            Tag = hotspot,
            Width = Math.Max(30, hotspot.Width * DiagramSurface.Width),
            Height = Math.Max(24, hotspot.Height * DiagramSurface.Height),
            MinWidth = 30,
            MinHeight = 24,
            IsEditMode = IsEditMode,
            IsSelected = selected,
            EditToolTipText = $"{hotspot.Name} · {fileCount} 个文件"
        };
        card.ResizeDelta += (_, args) =>
        {
            hotspot.Width = Math.Clamp(
                (card.ActualWidth + args.HorizontalChange) / DiagramSurface.Width,
                30 / DiagramSurface.Width,
                1 - hotspot.X);
            hotspot.Height = Math.Clamp(
                (card.ActualHeight + args.VerticalChange) / DiagramSurface.Height,
                24 / DiagramSurface.Height,
                1 - hotspot.Y);
            card.Width = hotspot.Width * DiagramSurface.Width;
            card.Height = hotspot.Height * DiagramSurface.Height;
        };
        card.ResizeCompleted += (_, _) => SaveProductAndRender();
        card.MouseLeftButtonDown += HotspotCard_OnMouseLeftButtonDown;
        card.MouseMove += HotspotCard_OnMouseMove;
        card.MouseLeftButtonUp += HotspotCard_OnMouseLeftButtonUp;
        card.DragOver += HotspotCard_OnDragOver;
        card.Drop += HotspotCard_OnDrop;
        return card;
    }

    private void HotspotCard_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not HotspotControl card)
        {
            return;
        }

        var hotspot = card.Hotspot;

        SelectHotspot(hotspot);
        if (IsEditMode && e.OriginalSource is DependencyObject source && !card.IsResizeHandleSource(source))
        {
            _movingHotspot = hotspot;
            _moveStart = e.GetPosition(HotspotCanvas);
            _moveOriginalX = hotspot.X;
            _moveOriginalY = hotspot.Y;
            card.CaptureMouse();
        }

        e.Handled = true;
    }

    private void HotspotCard_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsEditMode || _movingHotspot is null || e.LeftButton != MouseButtonState.Pressed || sender is not HotspotControl card)
        {
            return;
        }

        var position = e.GetPosition(HotspotCanvas);
        var delta = position - _moveStart;
        _movingHotspot.X = Math.Clamp(_moveOriginalX + delta.X / DiagramSurface.Width, 0, 1 - _movingHotspot.Width);
        _movingHotspot.Y = Math.Clamp(_moveOriginalY + delta.Y / DiagramSurface.Height, 0, 1 - _movingHotspot.Height);
        Canvas.SetLeft(card, _movingHotspot.X * DiagramSurface.Width);
        Canvas.SetTop(card, _movingHotspot.Y * DiagramSurface.Height);
        e.Handled = true;
    }

    private void HotspotCard_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_movingHotspot is null || sender is not HotspotControl card)
        {
            return;
        }

        card.ReleaseMouseCapture();
        _movingHotspot = null;
        SaveProductAndRender();
        e.Handled = true;
    }

    private void HotspotCard_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsEditMode && e.Data.GetDataPresent(GroupDragFormat) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void HotspotCard_OnDrop(object sender, DragEventArgs e)
    {
        if (!IsEditMode || _product is null || sender is not HotspotControl card ||
            e.Data.GetData(GroupDragFormat) is not string groupKey)
        {
            return;
        }

        var hotspot = card.Hotspot;

        if (!hotspot.GroupKeys.Contains(groupKey, StringComparer.CurrentCultureIgnoreCase))
        {
            hotspot.GroupKeys.Add(groupKey);
        }

        SelectHotspot(hotspot);
        if (SaveProductAndRender())
        {
            StatusText.Text = $"已将“{groupKey}”绑定到“{hotspot.Name}”。";
        }
        e.Handled = true;
    }

    private void DiagramSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_drawMode || _product is null)
        {
            return;
        }

        _drawStart = e.GetPosition(HotspotCanvas);
        _drawPreview = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 209, 76)),
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(65, 255, 209, 76))
        };
        HotspotCanvas.Children.Add(_drawPreview);
        Canvas.SetLeft(_drawPreview, _drawStart.X);
        Canvas.SetTop(_drawPreview, _drawStart.Y);
        DiagramSurface.CaptureMouse();
        e.Handled = true;
    }

    private void DiagramSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_drawPreview is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(HotspotCanvas);
        var x = Math.Min(_drawStart.X, current.X);
        var y = Math.Min(_drawStart.Y, current.Y);
        _drawPreview.Width = Math.Abs(current.X - _drawStart.X);
        _drawPreview.Height = Math.Abs(current.Y - _drawStart.Y);
        Canvas.SetLeft(_drawPreview, x);
        Canvas.SetTop(_drawPreview, y);
        e.Handled = true;
    }

    private void DiagramSurface_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawPreview is null || _product is null)
        {
            return;
        }

        DiagramSurface.ReleaseMouseCapture();
        var rectangle = _drawPreview;
        _drawPreview = null;
        var left = Canvas.GetLeft(rectangle);
        var top = Canvas.GetTop(rectangle);
        var width = rectangle.Width;
        var height = rectangle.Height;
        HotspotCanvas.Children.Remove(rectangle);

        if (width < 30 || height < 24)
        {
            CancelDrawMode("框选区域太小，已取消。");
            return;
        }

        var selectedGroup = GroupsList.SelectedItem as FileGroup;
        var defaultName = selectedGroup?.Key ?? "新部件";
        var dialog = new PromptDialog("部件名称", "请输入图片中这个部件的名称：", defaultName) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            CancelDrawMode("已取消创建热点。");
            return;
        }

        var hotspot = new PartHotspot
        {
            Name = dialog.Value.Trim(),
            X = Math.Clamp(left / DiagramSurface.Width, 0, 1),
            Y = Math.Clamp(top / DiagramSurface.Height, 0, 1),
            Width = Math.Clamp(width / DiagramSurface.Width, 0.03, 1),
            Height = Math.Clamp(height / DiagramSurface.Height, 0.03, 1)
        };
        if (selectedGroup is not null)
        {
            hotspot.GroupKeys.Add(selectedGroup.Key);
        }

        _product.Hotspots.Add(hotspot);
        _selectedHotspot = hotspot;
        CancelDrawMode($"已创建部件“{hotspot.Name}”。");
        SaveProductAndRender();
    }

    private void SelectHotspot(PartHotspot hotspot)
    {
        _selectedHotspot = hotspot;
        RefreshDetails();
        RefreshHotspotStyles();
    }

    private void RefreshHotspotStyles()
    {
        foreach (var card in HotspotCanvas.Children.OfType<HotspotControl>())
        {
            var hotspot = card.Hotspot;
            var selected = _selectedHotspot?.Id == hotspot.Id;
            card.IsSelected = selected;
        }
    }

    private void RefreshDetails()
    {
        if (_selectedHotspot is null)
        {
            SelectedHotspotText.Text = "请点击图片中的部件卡片。";
            HotspotActions.IsEnabled = false;
            FilesList.ItemsSource = null;
            GcodeFilesList.ItemsSource = null;
            EmptyFilesText.Text = "点击产品图中的部件查看文件";
            EmptyGcodeFilesText.Text = "点击产品图中的部件查看文件";
            EmptyFilesText.Visibility = Visibility.Visible;
            EmptyGcodeFilesText.Visibility = Visibility.Visible;
            return;
        }

        var visibleFiles = _files
            .Where(file => _selectedHotspot.GroupKeys.Contains(file.GroupKey, StringComparer.CurrentCultureIgnoreCase))
            .OrderBy(file => file.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SelectedHotspotText.Text = $"{_selectedHotspot.Name} · {visibleFiles.Count} 个文件\n" +
                                   (_selectedHotspot.GroupKeys.Count == 0
                                       ? "尚未绑定文件组"
                                       : string.Join("、", _selectedHotspot.GroupKeys));
        HotspotActions.IsEnabled = true;
        var standardFiles = visibleFiles.Where(file => !FileGroupingService.IsGcode3mf(file.FullPath)).ToList();
        var gcodeFiles = visibleFiles.Where(file => FileGroupingService.IsGcode3mf(file.FullPath)).ToList();
        FilesList.ItemsSource = standardFiles;
        GcodeFilesList.ItemsSource = gcodeFiles;
        EmptyFilesText.Text = "没有普通 .3mf 文件";
        EmptyGcodeFilesText.Text = "没有 .gcode.3mf 文件";
        EmptyFilesText.Visibility = standardFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyGcodeFilesText.Visibility = gcodeFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadThumbnailsAsync(visibleFiles, _selectedHotspot.Id);
    }

    private async Task LoadThumbnailsAsync(IEnumerable<ModelFile> files, string hotspotId)
    {
        var changed = false;
        foreach (var file in files.Where(file => file.Thumbnail is null).ToList())
        {
            file.Thumbnail = await _thumbnailService.LoadAsync(file.FullPath);
            changed = true;
            if (_selectedHotspot?.Id != hotspotId)
            {
                return;
            }
        }

        if (changed && _selectedHotspot?.Id == hotspotId)
        {
            FilesList.Items.Refresh();
            GcodeFilesList.Items.Refresh();
        }
    }

    private void NewProduct_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProductDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var product = _workspace.CreateProduct(dialog.ProductName, dialog.ImagePath, dialog.ModelsDirectory);
            LoadProductList(product.Id);
            EditModeToggle.IsChecked = true;
            StatusText.Text = "产品已创建。请从左侧选择文件组，然后点击“框选部件”。";
        }
        catch (Exception exception)
        {
            ShowError("创建产品失败", exception);
        }
    }

    private void ImportProductFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"选择包含 {PortableProductPackageService.ConfigDirectoryName}\\{PortableProductPackageService.ConfigFileName} 的产品 3MF 文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var product = _workspace.ImportProductFolder(dialog.FolderName);
            LoadProductList(product.Id);
            StatusText.Text = $"已从模型文件夹恢复产品“{product.Name}”、包装图、热点和文件映射。";
        }
        catch (Exception exception)
        {
            ShowError("导入产品文件夹失败", exception);
        }
    }

    private void RenameProduct_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null)
        {
            return;
        }

        var productId = _product.Id;
        var oldName = _product.Name;
        var dialog = new PromptDialog("修改产品名称", "只修改产品显示名称，不会改动目录或模型文件：", oldName)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newName = dialog.Value.Trim();
        if (string.Equals(newName, oldName, StringComparison.CurrentCulture))
        {
            return;
        }

        try
        {
            _workspace.RenameProduct(_product, newName);
            LoadProductList(productId);
            StatusText.Text = $"产品已改名为“{newName}”；模型目录和文件未改动。";
        }
        catch (ProductConfigConflictException exception)
        {
            MessageBox.Show(this,
                exception.Message + "\n\n已载入另一台电脑的最新设置，请重新修改产品名称。",
                "检测到其他电脑的修改",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ReloadSharedProductConfiguration(true);
        }
        catch (Exception exception)
        {
            _product.Name = oldName;
            ShowError("修改产品名称失败", exception);
        }
    }

    private void DeleteProduct_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || _slicingBusy) return;

        var name = _product.Name;
        var confirm = MessageBox.Show(this,
            $"只删除 PartMap 本机产品“{name}”的记录和本机包装图缓存。\n\n" +
            "不会删除模型目录里的 3MF / G-code，也不会删除共享“配置”文件夹。\n\n确定删除？",
            "删除产品", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _workspace.DeleteProduct(_product);
            _product = null;
            _selectedHotspot = null;
            LoadProductList();
            StatusText.Text = $"已删除本机产品“{name}”；模型文件和共享配置未删除。";
        }
        catch (Exception exception)
        {
            ShowError("删除产品失败", exception);
        }
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null)
        {
            return;
        }

        if (!IsModelDirectoryAvailable())
        {
            ShowModelDirectoryUnavailable(true);
            return;
        }

        ReloadFiles();
    }

    private void OpenModels_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null)
        {
            return;
        }

        var path = _workspace.ResolvePortablePath(_product.ModelRoot);
        if (!Directory.Exists(path))
        {
            ShowModelDirectoryUnavailable(true);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ChangeModels_OnClick(object sender, RoutedEventArgs e) => ChangeModelDirectory();

    private bool ChangeModelDirectory()
    {
        if (_product is null)
        {
            return false;
        }

        var dialog = new OpenFolderDialog
        {
            Title = $"为“{_product.Name}”选择原模型目录",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        try
        {
            DisposeModelWatcher();
            _workspace.RebindModelDirectory(_product, dialog.FolderName);
            ReloadFiles($"已直接绑定原模型目录：{dialog.FolderName}");
            StartWatcher();
            OpenModelsButton.ToolTip = $"打开原模型目录\n{dialog.FolderName}";
            return true;
        }
        catch (ProductConfigConflictException exception)
        {
            MessageBox.Show(this,
                exception.Message + "\n\n已载入另一台电脑的设置，请重新选择模型目录。",
                "检测到其他电脑的修改",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ReloadSharedProductConfiguration(true);
            return false;
        }
        catch (Exception exception)
        {
            ShowError("更换模型目录失败", exception);
            return false;
        }
    }

    private bool IsModelDirectoryAvailable()
    {
        return _product is not null &&
               Directory.Exists(_workspace.ResolvePortablePath(_product.ModelRoot));
    }

    private void ShowModelDirectoryUnavailable(bool offerRebind)
    {
        if (_product is null)
        {
            return;
        }

        DisposeModelWatcher();
        var missingPath = _workspace.ResolvePortablePath(_product.ModelRoot);
        _allFiles = [];
        _files = [];
        _groups = [];
        RefreshGroupList();
        RenderHotspots();
        RefreshDetails();
        StatusText.Text = $"原模型目录不可用：{missingPath}";

        if (!offerRebind)
        {
            return;
        }

        var result = MessageBox.Show(this,
            $"找不到原模型目录：\n{missingPath}\n\n是否现在重新选择目录？\n热点和映射配置会保留，PartMap 不会复制文件。",
            "模型目录不可用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            ChangeModelDirectory();
        }
    }

    private void EditMode_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
        {
            CancelDrawMode("已进入浏览模式。双击文件即可打开。", false);
        }

        UpdateModeLayout();
        RenderHotspots();
    }

    private void UpdateModeLayout()
    {
        if (GroupsPanel is null)
        {
            return;
        }

        var showEditor = IsEditMode && _product is not null;
        LeftPanelColumn.Width = showEditor ? new GridLength(280) : new GridLength(0);
        LeftSplitterColumn.Width = showEditor ? new GridLength(4) : new GridLength(0);
        GroupsPanel.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        LeftPanelSplitter.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        HotspotActions.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        DrawHotspotButton.IsEnabled = showEditor;
        OpenModelsButton.IsEnabled = _product is not null;
        ChangeModelsButton.IsEnabled = _product is not null;
        RenameProductButton.IsEnabled = _product is not null;
        DeleteProductButton.IsEnabled = _product is not null && !_slicingBusy;
        IncludeSubdirectoriesToggle.IsEnabled = _product is not null;
        ExcludedDirectoriesButton.IsEnabled = _product is not null;
    }

    private void ShowAllGroupsToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        RefreshGroupList();
    }

    private void IncludeSubdirectories_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressIncludeSubdirectoriesChange || _product is null)
        {
            return;
        }

        var changingProduct = _product;
        var previous = changingProduct.IncludeSubdirectories;
        var requested = IncludeSubdirectoriesToggle.IsChecked == true;
        if (previous == requested)
        {
            return;
        }

        changingProduct.IncludeSubdirectories = requested;
        DisposeModelWatcher();
        if (!TrySaveProduct())
        {
            if (ReferenceEquals(_product, changingProduct))
            {
                changingProduct.IncludeSubdirectories = previous;
            }
            UpdateProductOptions();
            StartWatcher();
            return;
        }

        ReloadFiles(requested
            ? "已开启子目录读取，模型列表和监控范围已更新。"
            : "已关闭子目录读取，现在只显示和监控模型根目录。", true);
        StartWatcher();
    }

    private void UpdateProductOptions()
    {
        _suppressIncludeSubdirectoriesChange = true;
        IncludeSubdirectoriesToggle.IsEnabled = _product is not null;
        IncludeSubdirectoriesToggle.IsChecked = _product?.IncludeSubdirectories ?? false;
        RenameProductButton.IsEnabled = _product is not null;
        DeleteProductButton.IsEnabled = _product is not null && !_slicingBusy;
        ExcludedDirectoriesButton.IsEnabled = _product is not null;
        var excluded = _product?.ExcludedDirectories ?? [];
        ExcludedDirectoriesButton.Content = excluded.Count == 0
            ? "排除文件夹…"
            : $"排除文件夹 ({excluded.Count})";
        ExcludedDirectoriesButton.ToolTip = excluded.Count == 0
            ? "选择读取子目录时需要忽略的文件夹"
            : "当前排除：\n" + string.Join("\n", excluded);
        _suppressIncludeSubdirectoriesChange = false;
    }

    private void ExcludedDirectories_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || !IsModelDirectoryAvailable())
        {
            ShowModelDirectoryUnavailable(true);
            return;
        }

        var changingProduct = _product;
        var previous = (changingProduct.ExcludedDirectories ?? []).ToList();
        var modelRoot = _workspace.ResolvePortablePath(changingProduct.ModelRoot);
        var dialog = new ExcludedDirectoriesDialog(modelRoot, previous) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var requested = dialog.ExcludedDirectories.ToList();
        if (previous.SequenceEqual(requested, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        changingProduct.ExcludedDirectories = requested;
        DisposeModelWatcher();
        if (!TrySaveProduct())
        {
            if (ReferenceEquals(_product, changingProduct))
            {
                changingProduct.ExcludedDirectories = previous;
            }
            UpdateProductOptions();
            StartWatcher();
            return;
        }

        UpdateProductOptions();
        ReloadFiles(requested.Count == 0
            ? "已取消全部文件夹排除，模型列表已恢复。"
            : $"已排除 {requested.Count} 个子文件夹，模型列表已更新。", true);
        StartWatcher();
    }

    private void DrawHotspot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || !IsEditMode)
        {
            return;
        }

        _drawMode = !_drawMode;
        DrawHotspotButton.Content = _drawMode ? "取消框选" : "框选部件";
        DiagramSurface.Cursor = _drawMode ? Cursors.Cross : Cursors.Arrow;
        StatusText.Text = _drawMode
            ? "在包装图空白位置按住鼠标并拖出矩形。若已选中文件组，会自动绑定。"
            : "已取消框选。";
    }

    private void CancelDrawMode(string status, bool updateStatus = true)
    {
        _drawMode = false;
        DrawHotspotButton.Content = "框选部件";
        DiagramSurface.Cursor = Cursors.Arrow;
        if (updateStatus)
        {
            StatusText.Text = status;
        }
    }

    private void ZoomSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DiagramSurface is not null)
        {
            DiagramSurface.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
        }
    }

    private void GroupsList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _groupDragStart = e.GetPosition(GroupsList);
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(GroupsList, source) is ListBoxItem item)
        {
            GroupsList.SelectedItem = item.DataContext;
        }
    }

    private void GroupsList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsEditMode || e.LeftButton != MouseButtonState.Pressed || GroupsList.SelectedItem is not FileGroup group)
        {
            return;
        }

        var current = e.GetPosition(GroupsList);
        if (Math.Abs(current.X - _groupDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _groupDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(GroupDragFormat, group.Key);
        DragDrop.DoDragDrop(GroupsList, data, DragDropEffects.Link);
    }

    private void GroupsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsEditMode || _selectedHotspot is null || GroupsList.SelectedItem is not FileGroup group)
        {
            return;
        }

        if (!_selectedHotspot.GroupKeys.Contains(group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            _selectedHotspot.GroupKeys.Add(group.Key);
            if (SaveProductAndRender())
            {
                StatusText.Text = $"已将“{group.Key}”绑定到“{_selectedHotspot.Name}”。";
            }
        }
    }

    private void RenameGroup_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || GroupsList.SelectedItem is not FileGroup group)
        {
            MessageBox.Show(this, "请先选择一个待归类文件。", "重命名文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ModelFile? file;
        if (group.Files.Count == 1)
        {
            file = group.Files[0];
        }
        else
        {
            var picker = new SelectModelFileDialog(group.Files) { Owner = this };
            if (picker.ShowDialog() != true) return;
            file = picker.SelectedFile;
        }

        if (file is not null) RenameModelFile(file);
    }

    private void GroupsList_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        RenameGroup_OnClick(sender, e);
        e.Handled = true;
    }

    private void RenameHotspot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || _selectedHotspot is null)
        {
            return;
        }

        var dialog = new PromptDialog("修改部件名称", "请输入部件卡片名称：", _selectedHotspot.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value))
        {
            _selectedHotspot.Name = dialog.Value.Trim();
            SaveProductAndRender();
        }
    }

    private void DuplicateHotspot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || _selectedHotspot is null)
        {
            return;
        }

        var duplicate = new PartHotspot
        {
            Name = _selectedHotspot.Name + " 副本",
            X = Math.Clamp(_selectedHotspot.X + 0.03, 0, 1 - _selectedHotspot.Width),
            Y = Math.Clamp(_selectedHotspot.Y + 0.03, 0, 1 - _selectedHotspot.Height),
            Width = _selectedHotspot.Width,
            Height = _selectedHotspot.Height,
            GroupKeys = [.. _selectedHotspot.GroupKeys]
        };
        _product.Hotspots.Add(duplicate);
        _selectedHotspot = duplicate;
        SaveProductAndRender();
    }

    private void RemoveGroupFromHotspot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || _selectedHotspot is null)
        {
            return;
        }

        var selectedGroupKeys = GetSelectedFiles()
            .Select(file => file.GroupKey)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (selectedGroupKeys.Count == 0 && GroupsList.SelectedItem is FileGroup group)
        {
            selectedGroupKeys.Add(group.Key);
        }

        if (selectedGroupKeys.Count == 0)
        {
            MessageBox.Show(this, "先在右侧选择文件；程序会移除这些文件所属的组。", "移除文件组",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var hotspotName = _selectedHotspot.Name;
        var removed = FileGroupingService.UnbindGroupsFromHotspot(_selectedHotspot, selectedGroupKeys);
        if (removed.Count > 0 && SaveProductAndRender())
        {
            StatusText.Text = $"已从“{hotspotName}”移除 {removed.Count} 个文件组。";
        }
    }

    private void GroupsPanel_OnDragOver(object sender, DragEventArgs e)
    {
        if (_product is not null && _selectedHotspot is not null &&
            e.Data.GetDataPresent(InternalFileGroupKeysFormat))
        {
            e.Effects = DragDropEffects.Move;
            GroupsPanel.BorderBrush = (Brush)FindResource("AccentBrush");
            GroupsPanel.BorderThickness = new Thickness(2);
            StatusText.Text = $"释放鼠标，把所选文件组从“{_selectedHotspot.Name}”移回待归类。";
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void GroupsPanel_OnDrop(object sender, DragEventArgs e)
    {
        ResetGroupsPanelDropVisual();
        if (_product is null || _selectedHotspot is null ||
            e.Data.GetData(InternalFileGroupKeysFormat) is not string[] groupKeys)
        {
            return;
        }

        var hotspotName = _selectedHotspot.Name;
        var removed = FileGroupingService.UnbindGroupsFromHotspot(_selectedHotspot, groupKeys);
        if (removed.Count > 0 && SaveProductAndRender())
        {
            StatusText.Text = $"已把 {removed.Count} 个文件组从“{hotspotName}”移回待归类。";
        }

        e.Handled = true;
    }

    private void ResetGroupsPanelDropVisual()
    {
        GroupsPanel.BorderBrush = (Brush)FindResource("LineBrush");
        GroupsPanel.BorderThickness = new Thickness(1);
    }

    private void DeleteHotspot_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null || _selectedHotspot is null)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除图片上的“{_selectedHotspot.Name}”热点？\n模型文件不会被删除。",
                "删除热点", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _product.Hotspots.RemoveAll(item => item.Id == _selectedHotspot.Id);
        _selectedHotspot = null;
        SaveProductAndRender();
    }

    private void FilesList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedFile();

    private void FilesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox selectedList || selectedList.SelectedItems.Count == 0)
        {
            return;
        }

        var otherList = ReferenceEquals(selectedList, FilesList) ? GcodeFilesList : FilesList;
        otherList.UnselectAll();
    }

    private IReadOnlyList<ModelFile> GetSelectedFiles()
    {
        var activeList = GcodeFilesList.SelectedItems.Count > 0 ? GcodeFilesList : FilesList;
        return activeList.SelectedItems.Cast<ModelFile>().ToList();
    }

    private ModelFile? GetSelectedFile() => GetSelectedFiles().FirstOrDefault();

    private void Window_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetDataPresent(InternalFileDragFormat))
        {
            ClearExternalDropVisuals();
            return;
        }

        if (CanAcceptDroppedFiles(e.Data))
        {
            var directHotspot = FindVisualAncestor<HotspotControl>(e.OriginalSource as DependencyObject);
            var overFilesPanel = directHotspot is null && IsWithinVisual(e.OriginalSource as DependencyObject, FilesPanelDropTarget);
            SetExternalDropVisuals(directHotspot, overFilesPanel);
            e.Effects = DragDropEffects.Copy;
            StatusText.Text = directHotspot is not null
                ? $"释放鼠标，导入文件并绑定到“{directHotspot.Hotspot.Name}”。"
                : overFilesPanel && _selectedHotspot is not null
                    ? $"释放鼠标，导入文件并加入当前部件“{_selectedHotspot.Name}”。"
                    : overFilesPanel
                        ? "释放鼠标导入文件；先点击图片部件，可同时完成绑定。"
                        : "释放鼠标，把 3MF 文件复制到当前产品的模型根目录。";
        }
        else
        {
            ClearExternalDropVisuals();
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void Window_OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            ClearExternalDropVisuals();
        }
    }

    private async void Window_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetDataPresent(InternalFileDragFormat))
        {
            return;
        }

        var directHotspot = FindVisualAncestor<HotspotControl>(e.OriginalSource as DependencyObject);
        var overFilesPanel = directHotspot is null && IsWithinVisual(e.OriginalSource as DependencyObject, FilesPanelDropTarget);
        var targetHotspot = directHotspot?.Hotspot ?? (overFilesPanel ? _selectedHotspot : null);
        ClearExternalDropVisuals();
        e.Handled = true;
        if (!CanAcceptDroppedFiles(e.Data) || _product is null ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] sourcePaths)
        {
            return;
        }
        if (!IsModelDirectoryAvailable())
        {
            ShowModelDirectoryUnavailable(true);
            return;
        }

        var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
        FileImportResult result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = await Task.Run(() => _fileOperations.CopyIntoModelRoot(sourcePaths, modelRoot));
        }
        catch (Exception exception)
        {
            ShowError("拖入文件失败", exception);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (result.CopiedPaths.Count > 0)
        {
            var bindingSucceeded = false;
            var boundGroupCount = 0;
            if (targetHotspot is not null)
            {
                try
                {
                    var groupKeys = _groupingService.BindImportedFilesToHotspot(
                        _product, modelRoot, result.CopiedPaths, targetHotspot);
                    boundGroupCount = groupKeys.Count;
                    _selectedHotspot = targetHotspot;
                    bindingSucceeded = TrySaveProduct();
                }
                catch (Exception exception)
                {
                    ShowError("文件已导入，但绑定部件失败", exception);
                }
            }

            var status = targetHotspot is null
                ? $"已拖入 {result.CopiedPaths.Count} 个 3MF 文件；跳过 {result.Issues.Count} 个。"
                : bindingSucceeded
                    ? $"已导入 {result.CopiedPaths.Count} 个文件，并将 {boundGroupCount} 个文件组加入“{targetHotspot.Name}”。"
                    : $"已导入 {result.CopiedPaths.Count} 个文件，但未能保存到目标部件，请重新拖放绑定。";
            ReloadFiles(status, targetHotspot is null);
        }
        else
        {
            StatusText.Text = $"没有复制文件；跳过 {result.Issues.Count} 个。";
        }

        if (result.Issues.Count > 0)
        {
            var details = string.Join("\n", result.Issues.Take(8)
                .Select(issue => $"• {Path.GetFileName(issue.SourcePath)}：{issue.Reason}"));
            if (result.Issues.Count > 8)
            {
                details += $"\n• 另有 {result.Issues.Count - 8} 个文件未列出";
            }

            MessageBox.Show(this, details, "部分文件未导入", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void SetExternalDropVisuals(HotspotControl? directHotspot, bool overFilesPanel)
    {
        if (ReferenceEquals(_externalDropTarget, directHotspot) && _externalDropOverFilesPanel == overFilesPanel)
        {
            return;
        }
        _externalDropTarget = directHotspot;
        _externalDropOverFilesPanel = overFilesPanel;

        foreach (var card in HotspotCanvas.Children.OfType<HotspotControl>())
        {
            card.IsDropTarget = ReferenceEquals(card, directHotspot);
        }

        FilesPanelDropTarget.Background = overFilesPanel
            ? (Brush)FindResource("AccentSoftBrush")
            : Brushes.White;
        FilesPanelDropTarget.BorderBrush = overFilesPanel
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("LineBrush");
        FilesPanelDropTarget.BorderThickness = overFilesPanel ? new Thickness(2) : new Thickness(1);
    }

    private void ClearExternalDropVisuals() => SetExternalDropVisuals(null, false);

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(source)
            };
        }

        return null;
    }

    private static bool IsWithinVisual(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = source switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(source)
            };
        }

        return false;
    }

    private bool CanAcceptDroppedFiles(IDataObject data)
    {
        if (_product is null || data.GetDataPresent(InternalFileDragFormat) ||
            !data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        // DragOver fires continuously. Keep it UI-only; network/file existence is validated once on Drop.
        return paths.Any(path => string.Equals(Path.GetExtension(path), ".3mf", StringComparison.OrdinalIgnoreCase));
    }

    private void FilesList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox sourceList)
        {
            _fileDragStart = e.GetPosition(sourceList);
        }
    }

    private void FilesList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox sourceList || e.LeftButton != MouseButtonState.Pressed ||
            sourceList.SelectedItems.Count == 0)
        {
            return;
        }

        var current = e.GetPosition(sourceList);
        if (Math.Abs(current.X - _fileDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _fileDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var selectedFiles = sourceList.SelectedItems.Cast<ModelFile>().ToList();
        var paths = selectedFiles.Select(file => file.FullPath).Where(File.Exists).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        var collection = new StringCollection();
        collection.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(collection);
        data.SetData(InternalFileDragFormat, true);
        data.SetData(InternalFileGroupKeysFormat, selectedFiles
            .Select(file => file.GroupKey)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray());
        DragDrop.DoDragDrop(sourceList, data, DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move);
        ResetGroupsPanelDropVisual();
    }

    private void FilesList_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            RenameSelectedFile();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenSelectedFile();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelectedFiles();
            e.Handled = true;
        }
    }

    private void OpenFile_OnClick(object sender, RoutedEventArgs e) => OpenSelectedFile();

    private async void OneClickSlice_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null) return;
        if (_slicingBusy)
        {
            ShowBatchSlicingWindow();
            StatusText.Text = "已有切片任务正在执行。";
            return;
        }
        var selected = FilesList.SelectedItems.Cast<ModelFile>()
            .Where(file => !FileGroupingService.IsGcode3mf(file.FullPath)).ToList();
        if (selected.Count != 1)
        {
            MessageBox.Show(this, "请在“普通 3MF”里只选择一个文件再切片。", "一键切片",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var source = selected[0];
        var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
        var plateId = 0;
        var plateName = "";
        try
        {
            var plates = _threeMfMetadataService.ReadPlates(source.FullPath);
            if (plates.Count > 1)
            {
                var duplicateName = plates.Where(plate => !string.IsNullOrWhiteSpace(plate.Name))
                    .GroupBy(plate => plate.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1)?.Key;
                if (duplicateName is not null)
                {
                    MessageBox.Show(this, $"这个 3MF 里有多个板都叫“{duplicateName}”。请先在 Bambu Studio 中改成不同名称。",
                        "板名称重复", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var dialog = new PlateSelectionDialog(plates) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.SelectedChoice is null) return;
                if (dialog.SelectedChoice.IsAll)
                {
                    if (!TryCreatePlateRequests(source, plates, out var requests)) return;
                    await RunBatchSlicingAsync(requests, _product, _selectedHotspot?.Id, modelRoot);
                    return;
                }
                if (string.IsNullOrWhiteSpace(dialog.SelectedChoice.Name))
                {
                    MessageBox.Show(this, "这个板还没有自定义名称，请先在 Bambu Studio 中命名后再切片。",
                        "板未命名", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                plateId = dialog.SelectedChoice.PlateId;
                plateName = dialog.SelectedChoice.Name;
            }
        }
        catch (Exception exception)
        {
            ShowError("读取板信息失败", exception);
            return;
        }

        SetSlicingBusy(true);
        StatusText.Text = $"正在切片：{source.DisplayName}{(plateName.Length > 0 ? $" · {plateName}" : "")}…";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var result = await _oneClickSlicingService.GenerateAsync(source.FullPath, modelRoot, plateId, plateName);
            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.TargetPath))
            {
                MessageBox.Show(this, result.Message, "一键切片失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = result.Message;
                return;
            }

            var relative = FileGroupingService.NormalizeRelative(Path.GetRelativePath(modelRoot, result.TargetPath));
            var selectedHotspotId = _selectedHotspot?.Id;
            _product = _workspace.UpsertFileBinding(_product, relative, source.GroupKey);
            _selectedHotspot = _product.Hotspots.FirstOrDefault(item => item.Id == selectedHotspotId);
            var cleanupNote = result.ArchivedPaths.Count > 0
                ? $"；旧实体已清理 {result.ArchivedPaths.Count} 个"
                : string.Empty;
            ReloadFiles($"{result.Message}{cleanupNote}");
        }
        catch (Exception exception)
        {
            ShowError("一键切片失败", exception);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            SetSlicingBusy(false);
        }
    }

    private async void BatchSlice_OnClick(object sender, RoutedEventArgs e)
    {
        if (_product is null) return;
        if (_slicingBusy)
        {
            ShowBatchSlicingWindow();
            StatusText.Text = "已有切片任务正在执行。";
            return;
        }

        var selected = FilesList.SelectedItems.Cast<ModelFile>()
            .Where(file => !FileGroupingService.IsGcode3mf(file.FullPath))
            .GroupBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请在“普通 3MF”里选择要排队切片的文件。", "批量切片",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var requests = new List<BatchSliceRequest>();
        try
        {
            foreach (var file in selected)
            {
                var plates = _threeMfMetadataService.ReadPlates(file.FullPath);
                if (plates.Count <= 1)
                {
                    requests.Add(new BatchSliceRequest(file.FullPath, file.DisplayName, file.GroupKey));
                }
                else
                {
                    if (!TryCreatePlateRequests(file, plates, out var plateRequests)) return;
                    requests.AddRange(plateRequests);
                }
            }
        }
        catch (Exception exception)
        {
            ShowError("读取板信息失败", exception);
            return;
        }

        var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
        await RunBatchSlicingAsync(requests, _product, _selectedHotspot?.Id, modelRoot);
    }

    private bool TryCreatePlateRequests(ModelFile source, IReadOnlyList<ThreeMfPlateInfo> plates,
        out List<BatchSliceRequest> requests)
    {
        requests = [];
        var unnamed = plates.Where(plate => string.IsNullOrWhiteSpace(plate.Name)).ToList();
        if (unnamed.Count > 0)
        {
            MessageBox.Show(this,
                $"“{source.DisplayName}”有 {unnamed.Count} 个板没有自定义名称。请先在 Bambu Studio 中给这些板命名，再切片。",
                "板未命名", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var duplicateName = plates.GroupBy(plate => plate.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            MessageBox.Show(this, $"“{source.DisplayName}”里有多个板都叫“{duplicateName}”。请先改成不同名称。",
                "板名称重复", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        requests = plates.Select(plate => new BatchSliceRequest(
            source.FullPath, $"{source.DisplayName} · {plate.Name}", source.GroupKey, plate.PlateId, plate.Name)).ToList();
        return true;
    }

    private async Task RunBatchSlicingAsync(IReadOnlyList<BatchSliceRequest> requests,
        ProductConfig queuedProduct, string? selectedHotspotId, string modelRoot)
    {
        var queuedProductId = queuedProduct.Id;
        var window = EnsureBatchSlicingWindow();
        window.Prepare(requests);
        ShowBatchSlicingWindow();
        _batchSlicingActive = true;
        SetSlicingBusy(true);
        StatusText.Text = $"已开始批量切片：{requests.Count} 个任务。";

        try
        {
            var progress = new Progress<BatchSliceProgress>(window.ApplyProgress);
            var results = await Task.Run(() => _batchSlicingService.RunAsync(requests, modelRoot, progress));
            var completed = results.Where(item =>
                item.State == BatchSliceState.Completed && !string.IsNullOrWhiteSpace(item.TargetPath)).ToList();
            ProductConfig? updatedProduct = null;
            if (completed.Count > 0)
            {
                var updates = completed.Select(item =>
                    (FileGroupingService.NormalizeRelative(Path.GetRelativePath(modelRoot, item.TargetPath!)),
                        item.Request.GroupKey)).ToList();
                updatedProduct = await Task.Run(() => _workspace.UpsertFileBindings(queuedProduct, updates));
            }

            var failed = results.Count(item => item.State == BatchSliceState.Failed);
            window.Finish(completed.Count, failed);
            if (_product?.Id == queuedProductId)
            {
                if (updatedProduct is not null)
                {
                    _product = updatedProduct;
                    _selectedHotspot = _product.Hotspots.FirstOrDefault(item => item.Id == selectedHotspotId);
                }
                ReloadFiles($"批量切片完成：成功 {completed.Count}，失败 {failed}。");
            }
            else
            {
                StatusText.Text = $"后台批量切片完成：成功 {completed.Count}，失败 {failed}。";
            }
        }
        catch (Exception exception)
        {
            ShowError("批量切片失败", exception);
        }
        finally
        {
            _batchSlicingActive = false;
            SetSlicingBusy(false);
        }
    }

    private BatchSlicingWindow EnsureBatchSlicingWindow()
    {
        if (_batchSlicingWindow is not null) return _batchSlicingWindow;
        _batchSlicingWindow = new BatchSlicingWindow { Owner = this };
        return _batchSlicingWindow;
    }

    private void ShowBatchSlicingWindow()
    {
        if (_batchSlicingWindow is null) return;
        if (!_batchSlicingWindow.IsVisible) _batchSlicingWindow.Show();
        _batchSlicingWindow.Activate();
    }

    private void SetSlicingBusy(bool busy)
    {
        _slicingBusy = busy;
        OneClickSliceButton.IsEnabled = !busy;
        BatchSliceButton.IsEnabled = !busy || _batchSlicingActive;
        DeleteProductButton.IsEnabled = !busy && _product is not null;
        BatchSliceButton.Content = _batchSlicingActive ? "查看切片进度" : "批量切片";
    }

    private void RenameFile_OnClick(object sender, RoutedEventArgs e) => RenameSelectedFile();
    private void DeleteFile_OnClick(object sender, RoutedEventArgs e) => DeleteSelectedFiles();
    private void ArchiveFile_OnClick(object sender, RoutedEventArgs e) => ArchiveSelectedFiles();

    private void OpenSelectedFile()
    {
        if (GetSelectedFile() is not ModelFile file)
        {
            return;
        }

        try
        {
            _fileOperations.Open(file);
        }
        catch (Exception exception)
        {
            ShowError("无法打开模型", exception);
        }
    }

    private void RenameSelectedFile()
    {
        if (GetSelectedFile() is ModelFile file) RenameModelFile(file);
    }

    private void RenameModelFile(ModelFile file)
    {
        if (_product is null) return;
        var dialog = new PromptDialog("重命名 3MF", "输入新的文件名；省略扩展名时会自动补上 .3mf：",
            Path.GetFileName(file.FullPath)) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
            var oldRelative = file.RelativePath;
            var newRelative = _fileOperations.Rename(file, dialog.Value, modelRoot);
            var selectedHotspotId = _selectedHotspot?.Id;
            _product = _workspace.UpdateFileBindingAfterRename(_product, oldRelative, newRelative);
            _selectedHotspot = _product.Hotspots.FirstOrDefault(item => item.Id == selectedHotspotId);
            ReloadFiles($"已重命名为“{Path.GetFileName(newRelative)}”。");
        }
        catch (Exception exception)
        {
            ShowError("重命名失败", exception);
        }
    }

    private void RevealFile_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetSelectedFile() is not ModelFile file)
        {
            return;
        }

        try
        {
            _fileOperations.Reveal(file);
        }
        catch (Exception exception)
        {
            ShowError("无法打开所在文件夹", exception);
        }
    }

    private void CopyPath_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetSelectedFile() is ModelFile file)
        {
            Clipboard.SetText(file.FullPath);
            StatusText.Text = "文件路径已复制。";
        }
    }

    private void DeleteSelectedFiles()
    {
        var selectedItems = GetSelectedFiles();
        if (_product is null || selectedItems.Count == 0)
        {
            return;
        }

        var selectedFiles = selectedItems
            .GroupBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var description = selectedFiles.Count == 1
            ? $"“{selectedFiles[0].DisplayName}”"
            : $"选中的 {selectedFiles.Count} 个文件";
        var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
        var isNetworkPath = FileOperationsService.IsNetworkPath(modelRoot);
        var confirmationText = isNetworkPath
            ? $"将永久删除共享目录中的{description}？\n\n网络共享文件通常不会进入本机回收站。请确认服务器或 NAS 已开启回收站、快照或备份。"
            : $"将{description}移到 Windows 回收站？\n\n删除后可以从回收站恢复。";
        var confirmation = MessageBox.Show(this,
            confirmationText,
            isNetworkPath ? "永久删除共享文件" : "删除模型文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var deleted = _fileOperations.DeleteToRecycleBin(selectedFiles, modelRoot);
            ReloadFiles(isNetworkPath
                ? $"已删除共享目录中的 {deleted.Count} 个文件。"
                : $"已将 {deleted.Count} 个文件移到回收站。");
        }
        catch (Exception exception)
        {
            ShowError("删除文件失败", exception);
            if (IsModelDirectoryAvailable())
            {
                ReloadFiles();
            }
        }
    }

    private void ArchiveSelectedFiles()
    {
        var selectedFiles = GetSelectedFiles();
        if (_product is null || selectedFiles.Count == 0)
        {
            return;
        }

        var confirmation = MessageBox.Show(this,
            $"把选中的 {selectedFiles.Count} 个文件移动到“{FileGroupingService.HistoryDirectoryName}”文件夹？\n\n" +
            "文件名会加入当天日期；同名时自动追加 _v1、_v2。历史文件不会再显示在 PartMap 中。",
            "归到历史",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
            var result = _fileOperations.ArchiveToHistory(selectedFiles, modelRoot);
            if (result.ArchivedPaths.Count > 0)
            {
                ReloadFiles($"已将 {result.ArchivedPaths.Count} 个文件归到“历史”；跳过 {result.Issues.Count} 个。", true);
            }
            else
            {
                StatusText.Text = $"没有归档文件；跳过 {result.Issues.Count} 个。";
            }

            if (result.Issues.Count > 0)
            {
                var details = string.Join("\n", result.Issues.Take(8)
                    .Select(issue => $"• {Path.GetFileName(issue.SourcePath)}：{issue.Reason}"));
                if (result.Issues.Count > 8)
                {
                    details += $"\n• 另有 {result.Issues.Count - 8} 个文件未列出";
                }
                MessageBox.Show(this, details, "部分文件未归档", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            ShowError("归档文件失败", exception);
            if (IsModelDirectoryAvailable())
            {
                ReloadFiles();
            }
        }
    }

    private bool SaveProductAndRender()
    {
        if (_product is null)
        {
            return false;
        }

        if (!TrySaveProduct())
        {
            return false;
        }

        RefreshGroupList();
        RenderHotspots();
        RefreshDetails();
        return true;
    }

    private bool TrySaveProduct()
    {
        if (_product is null)
        {
            return false;
        }

        try
        {
            _workspace.SaveProduct(_product);
            return true;
        }
        catch (ProductConfigConflictException exception)
        {
            MessageBox.Show(this,
                exception.Message + "\n\n已载入共享目录中的最新版本，请重新执行刚才的修改。",
                "检测到其他电脑的修改",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ReloadSharedProductConfiguration(true);
            return false;
        }
        catch (Exception exception)
        {
            ShowError("保存产品配置失败", exception);
            return false;
        }
    }

    private void StartWatcher()
    {
        if (_product is null)
        {
            return;
        }

        var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
        if (!Directory.Exists(modelRoot))
        {
            ShowModelDirectoryUnavailable(false);
            return;
        }

        _watcher = new FileSystemWatcher(modelRoot, "*.3mf")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Created += ModelDirectory_OnChanged;
        _watcher.Deleted += ModelDirectory_OnChanged;
        _watcher.Changed += ModelDirectory_OnChanged;
        _watcher.Renamed += ModelDirectory_OnChanged;
        _watcher.Error += (_, _) => Dispatcher.BeginInvoke(() => ReloadFiles("共享目录监控已重新同步。"));
    }

    private void StartConfigWatcher()
    {
        if (_product is null || string.IsNullOrWhiteSpace(_product.ConfigPath))
        {
            return;
        }

        _configPollTimer.Start();
        try
        {
            var directory = Path.GetDirectoryName(_product.ConfigPath)!;
            _configWatcher = new FileSystemWatcher(directory, Path.GetFileName(_product.ConfigPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _configWatcher.Created += SharedConfig_OnChanged;
            _configWatcher.Changed += SharedConfig_OnChanged;
            _configWatcher.Renamed += SharedConfig_OnChanged;
            _configWatcher.Error += (_, _) => Dispatcher.BeginInvoke(() => ReloadSharedProductConfiguration());
        }
        catch (IOException)
        {
            StatusText.Text = "共享目录实时通知不可用，将每 3 秒自动同步配置。";
        }
    }

    private void SharedConfig_OnChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _configWatcherDebounce.Stop();
            _configWatcherDebounce.Start();
        });
    }

    private void ReloadSharedProductConfiguration(bool force = false)
    {
        if (_product is null || string.IsNullOrWhiteSpace(_product.ConfigPath))
        {
            return;
        }

        try
        {
            var configPath = _product.ConfigPath;
            var latest = _configStore.Load<ProductConfig>(configPath);
            if (latest is null || (!force && latest.Revision <= _product.Revision))
            {
                return;
            }

            var selectedHotspotId = _selectedHotspot?.Id;
            latest.ConfigPath = configPath;
            _product = latest;
            _selectedHotspot = latest.Hotspots.FirstOrDefault(item => item.Id == selectedHotspotId);
            OpenModelsButton.ToolTip = $"打开原模型目录\n{_workspace.ResolvePortablePath(latest.ModelRoot)}";
            UpdateProductOptions();

            DisposeModelWatcher();
            LoadDiagram();
            if (IsModelDirectoryAvailable())
            {
                ReloadFiles("已同步另一台电脑保存的热点与映射。共享协作正常。");
                StartWatcher();
            }
            else
            {
                ShowModelDirectoryUnavailable(false);
            }
        }
        catch (IOException)
        {
            // 共享文件可能正处于原子替换瞬间，下次监控或轮询会再次读取。
        }
        catch (Exception exception)
        {
            StatusText.Text = $"同步共享配置失败，将自动重试：{exception.Message}";
        }
    }

    private void ModelDirectory_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (_product is not null && IsModelDirectoryAvailable())
        {
            var modelRoot = _workspace.ResolvePortablePath(_product.ModelRoot);
            var relative = FileGroupingService.NormalizeRelative(Path.GetRelativePath(modelRoot, e.FullPath));
            var inGcode = relative.Equals(FileGroupingService.GcodeDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                          relative.StartsWith(FileGroupingService.GcodeDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
            if (FileGroupingService.IsHistoryPath(relative)) return;
            if (!inGcode && (FileGroupingService.IsPathExcluded(relative, _product.ExcludedDirectories ?? []) ||
                             (!_product.IncludeSubdirectories && relative.Contains('/'))))
            {
                return;
            }
        }

        Dispatcher.BeginInvoke(() =>
        {
            _watcherDebounce.Stop();
            _watcherDebounce.Start();
        });
    }

    private void DisposeModelWatcher()
    {
        _watcherDebounce.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    private void DisposeProductWatchers()
    {
        DisposeModelWatcher();
        _configWatcherDebounce.Stop();
        _configPollTimer.Stop();
        _configWatcher?.Dispose();
        _configWatcher = null;
    }

    private void ShowEmptyState()
    {
        DisposeProductWatchers();
        _product = null;
        _allFiles = [];
        _files = [];
        _groups = [];
        _selectedHotspot = null;
        DiagramImage.Source = null;
        DiagramSurface.Width = 0;
        DiagramSurface.Height = 0;
        HotspotCanvas.Children.Clear();
        GroupsList.ItemsSource = null;
        FilesList.ItemsSource = null;
        GcodeFilesList.ItemsSource = null;
        EmptyGroupsText.Visibility = Visibility.Visible;
        EmptyFilesText.Visibility = Visibility.Visible;
        EmptyGcodeFilesText.Visibility = Visibility.Visible;
        WelcomePanel.Visibility = Visibility.Visible;
        UpdateProductOptions();
        UpdateModeLayout();
        StatusText.Text = "请选择或新建产品。";
    }

    private void ShowError(string title, Exception exception)
    {
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        StatusText.Text = $"{title}：{exception.Message}";
    }
}
