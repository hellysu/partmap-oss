using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using PartMap.Models;

namespace PartMap.Services;

public sealed class FileOperationsService
{
    private readonly Action<string> _recycleFile;
    private readonly SliceParameterHistoryService _parameterHistory;

    public FileOperationsService(Action<string>? recycleFile = null, SliceParameterHistoryService? parameterHistory = null)
    {
        _parameterHistory = parameterHistory ?? new SliceParameterHistoryService();
        _recycleFile = recycleFile ?? (path => FileSystem.DeleteFile(
            path,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException));
    }

    public void Open(ModelFile file)
    {
        EnsureExists(file.FullPath);
        Process.Start(new ProcessStartInfo(file.FullPath) { UseShellExecute = true });
    }

    public void Reveal(ModelFile file)
    {
        EnsureExists(file.FullPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{file.FullPath}\"",
            UseShellExecute = true
        });
    }

    public string Rename(ModelFile file, string requestedName, string modelRoot)
    {
        EnsureExists(file.FullPath);
        var trimmed = requestedName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("文件名不能为空。");
        }

        if (!trimmed.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += ".3mf";
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("文件名包含 Windows 不允许的字符。");
        }

        var stem = Path.GetFileNameWithoutExtension(trimmed).TrimEnd('.', ' ');
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        if (string.IsNullOrWhiteSpace(stem) || reserved.Contains(stem))
        {
            throw new InvalidOperationException("该文件名是 Windows 保留名称。");
        }

        var destination = Path.Combine(Path.GetDirectoryName(file.FullPath)!, stem + ".3mf");
        if (File.Exists(destination) && !string.Equals(destination, file.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("同一文件夹已经存在同名文件。");
        }

        if (!string.Equals(destination, file.FullPath, StringComparison.Ordinal))
        {
            File.Move(file.FullPath, destination);
        }

        return FileGroupingService.NormalizeRelative(Path.GetRelativePath(modelRoot, destination));
    }

    public FileImportResult CopyIntoModelRoot(IEnumerable<string> sourcePaths, string modelRoot)
    {
        var root = Path.GetFullPath(modelRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"模型目录不存在：{root}");
        }

        var result = new FileImportResult();
        foreach (var rawPath in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string source;
            try
            {
                source = Path.GetFullPath(rawPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                result.Issues.Add(new FileImportIssue(rawPath, "路径无效"));
                continue;
            }

            if (!File.Exists(source))
            {
                result.Issues.Add(new FileImportIssue(source, "文件不存在"));
                continue;
            }

            if (!string.Equals(Path.GetExtension(source), ".3mf", StringComparison.OrdinalIgnoreCase))
            {
                result.Issues.Add(new FileImportIssue(source, "仅支持 .3mf 文件"));
                continue;
            }

            if (IsWithinRoot(source, root))
            {
                result.Issues.Add(new FileImportIssue(source, "已经位于当前模型目录中"));
                continue;
            }

            var destinationDirectory = FileGroupingService.IsGcode3mf(source)
                ? Path.Combine(root, FileGroupingService.GcodeDirectoryName)
                : root;
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                result.Issues.Add(new FileImportIssue(source, "模型目录中已存在同名文件"));
                continue;
            }

            var temporary = Path.Combine(destinationDirectory, $".partmap-import-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(source, temporary, false);
                File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
                File.Move(temporary, destination, false);
                result.CopiedPaths.Add(destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.Issues.Add(new FileImportIssue(source, exception.Message));
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch
                    {
                    }
                }
            }
        }

        return result;
    }

    public GcodeFinalizeResult FinalizeSlicedGcode(string source3mf, string slicedGcode3mf,
        string modelRoot, BambuSlicePrintSummary summary, int? plateId = null, string plateName = "")
    {
        var root = Path.GetFullPath(modelRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var source = Path.GetFullPath(source3mf);
        var sliced = Path.GetFullPath(slicedGcode3mf);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"模型目录不存在：{root}");
        if (!File.Exists(source) || FileGroupingService.IsGcode3mf(source))
            throw new FileNotFoundException("一键切片源必须是当前普通 3MF。", source);
        if (!IsWithinRoot(source, root)) throw new UnauthorizedAccessException("切片源不在当前模型目录内。");
        var relative = FileGroupingService.NormalizeRelative(Path.GetRelativePath(root, source));
        if (FileGroupingService.IsHistoryPath(relative) ||
            relative.StartsWith(FileGroupingService.GcodeDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("历史文件或 G-code 目录中的文件不能作为切片源。");
        if (!File.Exists(sliced) || !FileGroupingService.IsGcode3mf(sliced))
            throw new InvalidDataException("Bambu Studio 没有生成有效的 .gcode.3mf 文件。");

        var sourceBaseName = Path.GetFileNameWithoutExtension(source).Trim();
        if (string.IsNullOrWhiteSpace(sourceBaseName)) throw new InvalidDataException("源 3MF 文件名无效。");
        var baseName = string.IsNullOrWhiteSpace(plateName)
            ? sourceBaseName
            : $"{sourceBaseName}-{SafeFileNameSegment(plateName)}";
        var gcodeRoot = Path.Combine(root, FileGroupingService.GcodeDirectoryName);
        var historyRoot = Path.Combine(gcodeRoot, FileGroupingService.HistoryDirectoryName);
        Directory.CreateDirectory(gcodeRoot);
        Directory.CreateDirectory(historyRoot);
        var existing = Directory.EnumerateFiles(gcodeRoot, "*.3mf", System.IO.SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(historyRoot, "*.3mf", System.IO.SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(historyRoot, "*.params.json", System.IO.SearchOption.TopDirectoryOnly));
        var maxVersion = existing.Select(path => TryGetSeriesVersion(Path.GetFileName(path), baseName))
            .Where(version => version.HasValue)
            .Select(version => version!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (maxVersion == int.MaxValue) throw new IOException("G-code 版本号已达到上限。");
        var version = maxVersion + 1;
        var finalName = $"{baseName}-v{version}_{summary.TimeLabel}_{summary.WeightLabel}.gcode.3mf";
        if (finalName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("自动生成的 G-code 文件名包含 Windows 不允许的字符。");
        var target = Path.Combine(gcodeRoot, finalName);
        var temporary = Path.Combine(gcodeRoot, $".partmap-slice-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sliced, temporary, false);
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary)) TryDeleteFile(temporary);
        }

        try
        {
            _parameterHistory.Save(source, sliced, historyRoot, finalName, plateId, plateName, version, summary);
        }
        catch
        {
            TryDeleteFile(target);
            throw;
        }

        var archived = new List<string>();
        var issues = new List<FileArchiveIssue>();
        foreach (var current in Directory.EnumerateFiles(gcodeRoot, "*.3mf", System.IO.SearchOption.TopDirectoryOnly).ToList())
        {
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase) ||
                !FileGroupingService.IsGcode3mf(current) ||
                !IsGcodeForSource(Path.GetFileName(current), baseName))
                continue;
            try
            {
                File.Delete(current);
                archived.Add(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new FileArchiveIssue(current, exception.Message));
            }
        }

        return new GcodeFinalizeResult
        {
            TargetPath = target,
            ArchivedPaths = archived,
            ArchiveIssues = issues
        };
    }

    public IReadOnlyList<string> DeleteToRecycleBin(IEnumerable<ModelFile> files, string modelRoot)
    {
        var root = Path.GetFullPath(modelRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"模型目录不存在：{root}");
        }

        var targets = files
            .GroupBy(file => Path.GetFullPath(file.FullPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (targets.Count == 0)
        {
            return [];
        }

        var validated = targets.Select(file =>
        {
            var fullPath = Path.GetFullPath(file.FullPath);
            var relative = Path.GetRelativePath(root, fullPath);
            var outsideRoot = relative.Equals("..", StringComparison.Ordinal) ||
                              relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                              Path.IsPathRooted(relative);
            if (outsideRoot)
            {
                throw new UnauthorizedAccessException("只能删除当前产品模型目录中的文件。");
            }

            EnsureExists(fullPath);
            return (FullPath: fullPath, RelativePath: FileGroupingService.NormalizeRelative(relative));
        }).ToList();

        foreach (var target in validated)
        {
            _recycleFile(target.FullPath);
        }

        return validated.Select(target => target.RelativePath).ToList();
    }

    public FileArchiveResult ArchiveToHistory(IEnumerable<ModelFile> files, string modelRoot, DateTime? archiveDate = null)
    {
        var root = Path.GetFullPath(modelRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"模型目录不存在：{root}");
        }

        var result = new FileArchiveResult();
        var validSources = new List<string>();
        foreach (var file in files.GroupBy(item => Path.GetFullPath(item.FullPath), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var source = Path.GetFullPath(file.FullPath);
            var relative = FileGroupingService.NormalizeRelative(Path.GetRelativePath(root, source));
            if (!IsWithinRoot(source, root))
            {
                result.Issues.Add(new FileArchiveIssue(source, "只能归档当前产品模型目录中的文件"));
            }
            else if (FileGroupingService.IsHistoryPath(relative))
            {
                result.Issues.Add(new FileArchiveIssue(source, "文件已经位于历史文件夹"));
            }
            else if (!File.Exists(source))
            {
                result.Issues.Add(new FileArchiveIssue(source, "文件不存在"));
            }
            else if (!string.Equals(Path.GetExtension(source), ".3mf", StringComparison.OrdinalIgnoreCase))
            {
                result.Issues.Add(new FileArchiveIssue(source, "仅支持 .3mf 文件"));
            }
            else if (FileGroupingService.IsGcode3mf(source))
            {
                result.Issues.Add(new FileArchiveIssue(source, "G-code 历史由切片参数记录自动管理，不再归档实体文件"));
            }
            else
            {
                validSources.Add(source);
            }
        }

        if (validSources.Count == 0)
        {
            return result;
        }

        var dateSuffix = (archiveDate ?? DateTime.Now).ToString("yyyy-MM-dd");
        foreach (var source in validSources)
        {
            var fileName = Path.GetFileName(source);
            var historyDirectory = Path.Combine(root, FileGroupingService.HistoryDirectoryName);
            Directory.CreateDirectory(historyDirectory);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var archived = false;
            for (var version = 0; version < 10_000; version++)
            {
                var versionSuffix = version == 0 ? string.Empty : $"_v{version}";
                var destination = Path.Combine(historyDirectory, $"{stem}_{dateSuffix}{versionSuffix}{extension}");
                try
                {
                    File.Move(source, destination, false);
                    result.ArchivedPaths.Add(destination);
                    archived = true;
                    break;
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // 同名可能来自另一台电脑并发归档，继续尝试下一个版本号。
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.Issues.Add(new FileArchiveIssue(source, exception.Message));
                    archived = true;
                    break;
                }
            }

            if (!archived)
            {
                result.Issues.Add(new FileArchiveIssue(source, "同名版本超过 9999 个，无法生成归档名称"));
            }
        }

        return result;
    }

    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int? TryGetGcodeVersion(string fileName, string baseName) =>
        TryGetSeriesVersion(fileName, baseName);

    private static int? TryGetSeriesVersion(string fileName, string baseName)
    {
        var stem = fileName.EndsWith(".params.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".params.json".Length]
            : FileGroupingService.GetDisplayName(fileName);
        var match = Regex.Match(stem,
            $"^{Regex.Escape(baseName)}[-_]v(?<version>\\d+)(?:_|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["version"].Value, out var version) ? version : null;
    }

    private static string SafeFileNameSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var text = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("板名称不能用于文件名，请先在 Bambu Studio 中给板命名。");
        return text;
    }

    private static bool IsGcodeForSource(string fileName, string baseName)
    {
        var stem = FileGroupingService.GetDisplayName(fileName);
        if (string.Equals(stem, baseName, StringComparison.OrdinalIgnoreCase)) return true;
        if (TryGetGcodeVersion(fileName, baseName).HasValue) return true;
        return Regex.IsMatch(stem,
            $"^{Regex.Escape(baseName)}[-_]\\d+(?:\\.\\d+)?h_\\d+(?:\\.\\d+)?g$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void EnsureExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文件已被移动或删除，请刷新列表。", path);
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}

public sealed class GcodeFinalizeResult
{
    public string TargetPath { get; init; } = "";
    public IReadOnlyList<string> ArchivedPaths { get; init; } = [];
    public IReadOnlyList<FileArchiveIssue> ArchiveIssues { get; init; } = [];
}

public sealed class FileImportResult
{
    public List<string> CopiedPaths { get; } = [];
    public List<FileImportIssue> Issues { get; } = [];
}

public sealed record FileImportIssue(string SourcePath, string Reason);

public sealed class FileArchiveResult
{
    public List<string> ArchivedPaths { get; } = [];
    public List<FileArchiveIssue> Issues { get; } = [];
}

public sealed record FileArchiveIssue(string SourcePath, string Reason);
