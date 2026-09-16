using System.Text.Json;
using PartMap.Models;

namespace PartMap.Services;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup))
            {
                throw;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(backup), JsonOptions);
        }
    }

    public void Save<T>(string path, T value)
    {
        ExecuteLocked(path, () => SaveUnlocked(path, value));
    }

    public void ExecuteLocked(string path, Action action, TimeSpan? timeout = null)
    {
        using var lockStream = AcquireLock(path, timeout ?? TimeSpan.FromSeconds(8));
        action();
    }

    internal void SaveUnlocked<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var backup = path + ".bak";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));

            if (File.Exists(path))
            {
                File.Copy(path, backup, true);
                File.Move(temporary, path, true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static FileStream AcquireLock(string path, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lockPath = path + ".lock";
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                lastError = exception;
                Thread.Sleep(80);
            }
        }

        throw new TimeoutException("另一台电脑正在保存此配置，请稍后重试。", lastError);
    }
}
