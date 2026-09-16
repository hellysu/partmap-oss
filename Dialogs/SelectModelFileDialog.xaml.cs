using System.Windows;
using PartMap.Models;

namespace PartMap.Dialogs;

public partial class SelectModelFileDialog : Window
{
    public SelectModelFileDialog(IEnumerable<ModelFile> files)
    {
        InitializeComponent();
        FilesList.ItemsSource = files.OrderBy(file => file.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        FilesList.SelectedIndex = 0;
    }

    public ModelFile? SelectedFile => FilesList.SelectedItem as ModelFile;

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is null) return;
        DialogResult = true;
    }
}