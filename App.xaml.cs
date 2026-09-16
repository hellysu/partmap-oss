using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace PartMap;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteDiagnosticLog(args.Exception);
            MessageBox.Show(
                $"程序遇到未处理的错误：\n\n{args.Exception.Message}\n\n诊断日志：\n{DiagnosticLogPath}",
                "PartMap",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);

        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            WriteDiagnosticLog(exception);
            MessageBox.Show(
                $"PartMap 无法启动：\n\n{exception.Message}\n\n请确认已完整解压程序，并拥有共享目录的读写权限。\n诊断日志：\n{DiagnosticLogPath}",
                "PartMap 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string DiagnosticLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PartMap",
        "PartMap-startup.log");

    private static void WriteDiagnosticLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticLogPath)!);
            var details = new StringBuilder()
                .AppendLine($"Time: {DateTimeOffset.Now:O}")
                .AppendLine($"OS: {RuntimeInformation.OSDescription}")
                .AppendLine($"Architecture: {RuntimeInformation.OSArchitecture} / {RuntimeInformation.ProcessArchitecture}")
                .AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}")
                .AppendLine($"Executable: {Environment.ProcessPath}")
                .AppendLine(exception.ToString())
                .AppendLine(new string('-', 72))
                .ToString();
            File.AppendAllText(DiagnosticLogPath, details);
        }
        catch
        {
            // 诊断日志不能影响错误提示本身。
        }
    }
}
