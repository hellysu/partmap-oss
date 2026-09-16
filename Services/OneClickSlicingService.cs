using System.Security.Cryptography;

namespace PartMap.Services;

public sealed class OneClickSliceResult
{
    public bool Succeeded { get; init; }
    public string? TargetPath { get; init; }
    public IReadOnlyList<string> ArchivedPaths { get; init; } = [];
    public string Message { get; init; } = "";
}

public sealed class OneClickSlicingService
{
    private readonly BambuStudioSlicingService _slicer;
    private readonly FileOperationsService _files;

    public OneClickSlicingService(
        BambuStudioSlicingService? slicer = null,
        FileOperationsService? files = null)
    {
        _slicer = slicer ?? new BambuStudioSlicingService();
        _files = files ?? new FileOperationsService();
    }

    public Task<OneClickSliceResult> GenerateAsync(
        string source3mf,
        string modelRoot,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(source3mf, modelRoot, 0, "", cancellationToken);

    public async Task<OneClickSliceResult> GenerateAsync(
        string source3mf,
        string modelRoot,
        int plateId,
        string plateName,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source3mf) || FileGroupingService.IsGcode3mf(source3mf))
            return Failure("请选择一个存在的普通 3MF 文件。");
        if (string.IsNullOrWhiteSpace(modelRoot) || !Directory.Exists(modelRoot))
            return Failure("当前模型目录不可用，请重新绑定后再切片。");

        var root = Path.GetFullPath(modelRoot);
        var source = Path.GetFullPath(source3mf);
        var relative = FileGroupingService.NormalizeRelative(Path.GetRelativePath(root, source));
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return Failure("源 3MF 不在当前模型目录内，未开始切片。");
        if (FileGroupingService.IsHistoryPath(relative) ||
            relative.StartsWith(FileGroupingService.GcodeDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
            return Failure("历史文件或 G-code 目录中的文件不能作为一键切片源。");

        var tempRoot = Path.Combine(Path.GetTempPath(), "PartMap", "slicer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var frozenInput = Path.Combine(tempRoot, "source.3mf");
            if (!FreezeStableSource(source, frozenInput, out var freezeError)) return Failure(freezeError);

            var slicedOutput = Path.Combine(tempRoot, "slice.gcode.3mf");
            var sliced = await _slicer.SliceAsync(frozenInput, slicedOutput, plateId, cancellationToken);
            if (!sliced.Succeeded || sliced.Summary is null) return Failure(sliced.Message);

            var finalized = _files.FinalizeSlicedGcode(source, sliced.OutputPath, root, sliced.Summary,
                plateId == 0 ? null : plateId, plateName);
            return new OneClickSliceResult
            {
                Succeeded = true,
                TargetPath = finalized.TargetPath,
                ArchivedPaths = finalized.ArchivedPaths,
                Message = $"切片完成：{Path.GetFileName(finalized.TargetPath)}"
            };
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static bool FreezeStableSource(string source, string frozen, out string error)
    {
        error = "";
        try
        {
            var before = new FileInfo(source);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            File.Copy(source, frozen, false);
            var after = new FileInfo(source);
            if (after.Length != length || after.LastWriteTimeUtc != modified || new FileInfo(frozen).Length != length)
            {
                error = "源 3MF 正在保存，未开始切片；请稍后重试。";
                return false;
            }

            var frozenHash = ComputeSha256(frozen);
            var sourceHash = ComputeSha256(source);
            if (!string.Equals(frozenHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "源 3MF 在复制期间发生变化，未开始切片；请稍后重试。";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"读取源 3MF 失败：{exception.Message}";
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private static OneClickSliceResult Failure(string message) =>
        new() { Succeeded = false, Message = message };
}
