using System.Windows;
using System.Windows.Input;

namespace PartMap.Dialogs;

public partial class PromptDialog : Window
{
    public PromptDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string Value => ValueTextBox.Text;

    private void Accept_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void ValueTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
        }
    }
}
