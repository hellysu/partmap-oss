using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PartMap.Services;

namespace PartMap.Dialogs;

public partial class ExcludedDirectoriesDialog : Window
{
    private readonly string _modelRoot;

    public ExcludedDirectoriesDialog(string modelRoot, IEnumerable<string> excludedDirectories)
    {
        InitializeComponent();
        _modelRoot = Path.GetFullPath(modelRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ExcludedDirectories = new ObservableCollection<string>(excludedDirectories
            .Select(FileGroupingService.NormalizeExcludedDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase));
        DataContext = this;
        UpdateEmptyState();
    }

    public ObservableCollection<string> ExcludedDirectories { get; }

    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要从 PartMap 中排除的子文件夹",
            InitialDirectory = _modelRoot,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var invalid = new List<string>();
        foreach (var selectedPath in dialog.FolderNames)
        {
            var relative = Path.GetRelativePath(_modelRoot, Path.GetFullPath(selectedPath));
            var outsideRoot = relative.Equals("..", StringComparison.Ordinal) ||
                              relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                              Path.IsPathRooted(relative);
            if (outsideRoot || relative == ".")
            {
                invalid.Add(selectedPath);
                continue;
            }

            var normalized = FileGroupingService.NormalizeExcludedDirectory(relative);
            if (ExcludedDirectories.Any(existing => Covers(existing, normalized)))
            {
                continue;
            }

            foreach (var descendant in ExcludedDirectories.Where(existing => Covers(normalized, existing)).ToList())
            {
                ExcludedDirectories.Remove(descendant);
            }
            ExcludedDirectories.Add(normalized);
        }

        SortDirectories();
        UpdateEmptyState();
        if (invalid.Count > 0)
        {
            MessageBox.Show(this, "只能排除当前模型根目录内的子文件夹，不能排除根目录本身。",
                "部分选择未添加", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Remove_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in DirectoriesList.SelectedItems.Cast<string>().ToList())
        {
            ExcludedDirectories.Remove(item);
        }
        UpdateEmptyState();
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void SortDirectories()
    {
        var sorted = ExcludedDirectories.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToList();
        ExcludedDirectories.Clear();
        foreach (var path in sorted)
        {
            ExcludedDirectories.Add(path);
        }
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = ExcludedDirectories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static bool Covers(string parent, string candidate) =>
        candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
}
