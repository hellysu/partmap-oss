using System.Windows;
using PartMap.Services;

namespace PartMap.Dialogs;

public partial class PlateSelectionDialog : Window
{
    public sealed record PlateChoice(int PlateId, string Name, bool IsAll)
    {
        public string DisplayName => IsAll ? "全部板" : string.IsNullOrWhiteSpace(Name) ? $"未命名（ID {PlateId}）" : Name;
    }

    public PlateChoice? SelectedChoice { get; private set; }

    public PlateSelectionDialog(IReadOnlyList<ThreeMfPlateInfo> plates)
    {
        InitializeComponent();
        var items = new List<PlateChoice> { new(0, "", true) };
        items.AddRange(plates.Select(plate => new PlateChoice(plate.PlateId, plate.Name, false)));
        PlatesList.ItemsSource = items;
        PlatesList.SelectedIndex = 0;
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (PlatesList.SelectedItem is not PlateChoice choice) return;
        SelectedChoice = choice;
        DialogResult = true;
    }
}