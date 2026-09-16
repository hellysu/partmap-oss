using System.Windows;
using Microsoft.Win32;

namespace PartMap.Dialogs;

public partial class NewProductDialog : Window
{
    public NewProductDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string ProductName => NameTextBox.Text.Trim();
    public string ImagePath => ImagePathTextBox.Text;
    public string ModelsDirectory => ModelsPathTextBox.Text;

    private void ChooseImage_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择产品包装图",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            ImagePathTextBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                NameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    private void ChooseModels_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 3MF 的目录",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            ModelsPathTextBox.Text = dialog.FolderName;
        }
    }

    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProductName))
        {
            MessageBox.Show(this, "请输入产品名称。", "新建产品", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(ImagePath))
        {
            MessageBox.Show(this, "请选择有效的包装图。", "新建产品", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(ModelsDirectory))
        {
            MessageBox.Show(this, "请选择有效的原模型目录。PartMap 将直接管理其中的文件。", "新建产品",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
