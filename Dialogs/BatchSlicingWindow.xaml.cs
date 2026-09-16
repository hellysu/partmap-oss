using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using PartMap.Services;

namespace PartMap.Dialogs;

public partial class BatchSlicingWindow : Window
{
    private readonly ObservableCollection<BatchSliceRow> _rows = [];
    private bool _allowClose;

    public BatchSlicingWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = _rows;
    }

    public void Prepare(IReadOnlyList<BatchSliceRequest> requests)
    {
        _rows.Clear();
        foreach (var request in requests)
        {
            _rows.Add(new BatchSliceRow(request.DisplayName));
        }

        QueueProgress.Maximum = Math.Max(1, requests.Count);
        QueueProgress.Value = 0;
        SummaryText.Text = $"已排队 {requests.Count} 个文件";
        HintText.Text = "切片在后台串行执行，可以继续使用主窗口。";
    }

    public void ApplyProgress(BatchSliceProgress progress)
    {
        if (progress.Index < 1 || progress.Index > _rows.Count) return;
        var row = _rows[progress.Index - 1];
        row.StatusText = progress.State switch
        {
            BatchSliceState.Waiting => "等待",
            BatchSliceState.Running => "切片中",
            BatchSliceState.Completed => "完成",
            BatchSliceState.Failed => "失败",
            _ => ""
        };
        row.Message = progress.Message;
        QueueList.Items.Refresh();

        QueueProgress.Value = progress.State == BatchSliceState.Running
            ? progress.Index - 1
            : progress.Index;
        SummaryText.Text = progress.State == BatchSliceState.Running
            ? $"正在切片 {progress.Index}/{progress.Total}：{progress.Request.DisplayName}"
            : $"已处理 {progress.Index}/{progress.Total}";
    }

    public void Finish(int succeeded, int failed)
    {
        QueueProgress.Value = QueueProgress.Maximum;
        SummaryText.Text = $"批量切片完成：成功 {succeeded}，失败 {failed}";
        HintText.Text = failed == 0 ? "全部完成。" : "失败项已保留在列表中，可查看原因。";
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void Hide_OnClick(object sender, RoutedEventArgs e) => Hide();

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private sealed class BatchSliceRow(string displayName)
    {
        public string DisplayName { get; } = displayName;
        public string StatusText { get; set; } = "等待";
        public string Message { get; set; } = "";
    }
}
