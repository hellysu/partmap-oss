using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace PartMap.Services;

public sealed record BambuSlicePrintSummary(string TimeLabel, string WeightLabel);

public sealed class BambuSliceResult
{
    public bool Succeeded { get; init; }
    public string OutputPath { get; init; } = "";
    public string Message { get; init; } = "";
    public int? ExitCode { get; init; }
    public BambuSlicePrintSummary? Summary { get; init; }
}

public sealed record SlicerProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface ISlicerProcessRunner
{
    Task<SlicerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class BambuStudioSlicingService
{
    public const string ExecutableEnvironmentVariable = "PARTMAP_BAMBU_STUDIO_PATH";
    private readonly ISlicerProcessRunner _runner;
    private readonly Func<string?> _resolveExecutable;
    private readonly TimeSpan _timeout;

    public BambuStudioSlicingService(
        ISlicerProcessRunner? runner = null,
        Func<string?>? resolveExecutable = null,
        TimeSpan? timeout = null)
    {
        _runner = runner ?? new DefaultSlicerProcessRunner();
        _resolveExecutable = resolveExecutable ?? ResolveExecutable;
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    public Task<BambuSliceResult> SliceAsync(
        string source3mf,
        string outputGcode3mf,
        CancellationToken cancellationToken = default) =>
        SliceAsync(source3mf, outputGcode3mf, 0, cancellationToken);

    public async Task<BambuSliceResult> SliceAsync(
        string source3mf,
        string outputGcode3mf,
        int plateId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source3mf) || FileGroupingService.IsGcode3mf(source3mf))
            return Failure("请选择一个存在的普通 3MF 文件。");
        if (!FileGroupingService.IsGcode3mf(outputGcode3mf))
            return Failure("切片输出必须使用 .gcode.3mf 文件名。");

        var executable = _resolveExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            return Failure($"未找到 Bambu Studio。请先安装 Bambu Studio，或设置 {ExecutableEnvironmentVariable}。");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputGcode3mf))!);
            if (File.Exists(outputGcode3mf)) File.Delete(outputGcode3mf);
            var arguments = BuildArguments(source3mf, outputGcode3mf, plateId);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            var process = await _runner.RunAsync(executable, arguments, timeoutCts.Token);
            if (process.ExitCode != 0)
            {
                return Failure(
                    $"Bambu Studio 切片失败（退出码 {process.ExitCode}）：{CompactError(process.StandardError, process.StandardOutput)}",
                    process.ExitCode);
            }

            if (!ValidateAndReadSummary(outputGcode3mf, out var summary, out var validationError))
                return Failure($"Bambu Studio 未生成可用的 G-code 3MF：{validationError}", process.ExitCode);

            return new BambuSliceResult
            {
                Succeeded = true,
                OutputPath = outputGcode3mf,
                ExitCode = process.ExitCode,
                Summary = summary,
                Message = "切片完成。"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure($"Bambu Studio 切片超过 {_timeout.TotalMinutes:0} 分钟，已停止；已有 G-code 未修改。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure($"切片失败：{exception.Message}");
        }
    }

    public static IReadOnlyList<string> BuildArguments(string source3mf, string outputGcode3mf) =>
        BuildArguments(source3mf, outputGcode3mf, 0);

    public static IReadOnlyList<string> BuildArguments(string source3mf, string outputGcode3mf, int plateId) =>
        ["--slice", plateId.ToString(CultureInfo.InvariantCulture), "--debug", "3", "--min-save", "--export-3mf",
            Path.GetFullPath(outputGcode3mf), Path.GetFullPath(source3mf)];

    public static bool ValidateAndReadSummary(string path, out BambuSlicePrintSummary summary, out string error)
    {
        summary = new BambuSlicePrintSummary("", "");
        error = "";
        if (!File.Exists(path))
        {
            error = "输出文件不存在。";
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Any(entry =>
                    entry.FullName.StartsWith("3D/Objects/", StringComparison.OrdinalIgnoreCase)))
            {
                error = "输出包含源模型几何，不是纯 G-code 3MF。";
                return false;
            }
            if (!archive.Entries.Any(entry => entry.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase)))
            {
                error = "缺少 3D model 数据。";
                return false;
            }

            var gcodeEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase));
            if (gcodeEntry is null)
            {
                error = "包内没有切片 G-code。";
                return false;
            }

            string? timeText = null;
            string? weightText = null;
            using var reader = new StreamReader(gcodeEntry.Open());
            while (!reader.EndOfStream && (timeText is null || weightText is null))
            {
                var line = reader.ReadLine() ?? "";
                if (timeText is null)
                {
                    var timeMatch = Regex.Match(line, @"total estimated time:\s*(?<value>[^;\r\n]+)",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (timeMatch.Success) timeText = timeMatch.Groups["value"].Value.Trim();
                }

                if (weightText is null)
                {
                    var weightMatch = Regex.Match(line,
                        @"total filament weight\s*\[g\]\s*:\s*(?<value>[0-9]+(?:\.[0-9]+)?(?:\s*,\s*[0-9]+(?:\.[0-9]+)?)*)",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (weightMatch.Success) weightText = weightMatch.Groups["value"].Value.Trim();
                }
            }

            if (!TryFormatTime(timeText, out var timeLabel))
            {
                error = "切片文件中找不到可识别的总打印时间。";
                return false;
            }
            var weights = (weightText ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (weights.Length == 0 || weights.Any(value =>
                    !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                error = "切片文件中找不到可识别的耗材克重。";
                return false;
            }
            var grams = weights.Sum(value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));

            var weightLabel = Math.Ceiling(grams)
                .ToString("0", CultureInfo.InvariantCulture) + "g";
            summary = new BambuSlicePrintSummary(timeLabel, weightLabel);
            return true;
        }
        catch (InvalidDataException)
        {
            error = "文件不是有效的 3MF/ZIP 包。";
            return false;
        }
        catch (IOException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 打印时间显示采用保守的半小时取整规则：
    /// 原始时间先向上到最近的 30 分钟点；与该点的差值 ≤ 12 分钟时再加 30 分钟
    /// （差值正好 0，即本身落在半小时点上，也加 30 分钟）。
    /// </summary>
    private static bool TryFormatTime(string? value, out string label)
    {
        label = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Regex.Match(value,
            @"^\s*(?:(?<h>\d+)h)?\s*(?:(?<m>\d+)m)?\s*(?:(?<s>\d+)s)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !match.Groups["h"].Success && !match.Groups["m"].Success && !match.Groups["s"].Success)
            return false;
        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var seconds = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        var totalMinutes = hours * 60d + minutes + seconds / 60d;
        var roundedMinutes = Math.Ceiling(totalMinutes / 30d) * 30d;
        var minutesToRounded = roundedMinutes - totalMinutes;
        if (minutesToRounded <= 12d)
        {
            roundedMinutes += 30d;
        }

        label = (roundedMinutes / 60d)
            .ToString("0.#", CultureInfo.InvariantCulture) + "h";
        return true;
    }

    public static string? ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable)?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        return SelectPreferredExecutable(RunningExecutableCandidates(), InstalledExecutableCandidates())
               ?? FindOnPath("bambu-studio.exe")
               ?? FindOnPath("BambuStudio.exe");
    }

    public static string? SelectPreferredExecutable(
        IEnumerable<string> runningExecutables, IEnumerable<string> installedCandidates)
    {
        return SelectNewestExisting(runningExecutables) ?? SelectNewestExisting(installedCandidates);
    }

    private static string? SelectNewestExisting(IEnumerable<string> candidates)
    {
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(GetExecutableVersion)
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static Version GetExecutableVersion(string path)
    {
        try
        {
            var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(text, out var version) ? version : new Version(0, 0);
        }
        catch
        {
            return new Version(0, 0);
        }
    }

    private static IEnumerable<string> RunningExecutableCandidates()
    {
        foreach (var processName in new[] { "bambu-studio", "BambuStudio" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { }
                    if (!string.IsNullOrWhiteSpace(path)) yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> InstalledExecutableCandidates()
    {
        foreach (var candidate in StandardExecutableCandidates()) yield return candidate;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            foreach (var folderName in new[] { "Program Files", "Program Files (x86)" })
            {
                var root = Path.Combine(drive.RootDirectory.FullName, folderName);
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> directories;
                try
                {
                    directories = Directory.EnumerateDirectories(root, "Bambu Studio*", SearchOption.TopDirectoryOnly).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var directory in directories)
                {
                    yield return Path.Combine(directory, "bambu-studio.exe");
                    yield return Path.Combine(directory, "BambuStudio.exe");
                }
            }
        }
    }

    private static IEnumerable<string> StandardExecutableCandidates()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        foreach (var root in roots.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            yield return Path.Combine(root, "Bambu Studio", "bambu-studio.exe");
            yield return Path.Combine(root, "Bambu Studio", "BambuStudio.exe");
            yield return Path.Combine(root, "Programs", "Bambu Studio", "bambu-studio.exe");
            yield return Path.Combine(root, "Programs", "Bambu Studio", "BambuStudio.exe");
        }
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = raw.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    private static BambuSliceResult Failure(string message, int? exitCode = null) =>
        new() { Succeeded = false, Message = message, ExitCode = exitCode };

    private static string CompactError(params string[] values)
    {
        var text = string.Join(" ", values.Where(item => !string.IsNullOrWhiteSpace(item)))
            .Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(text) ? "没有返回详细错误信息。" : text[..Math.Min(text.Length, 500)];
    }

    private sealed class DefaultSlicerProcessRunner : ISlicerProcessRunner
    {
        public async Task<SlicerProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Bambu Studio。");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                return new SlicerProcessResult(process.ExitCode, await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                throw;
            }
        }
    }
}
