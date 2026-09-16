using System.Runtime.InteropServices;
using System.Text;

namespace PartMap.Services;

public static class NetworkPathService
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;

    public static string ToStablePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return fullPath;
        }

        try
        {
            if (new DriveInfo(root).DriveType != DriveType.Network)
            {
                return fullPath;
            }

            var localDrive = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var capacity = 512;
            while (capacity <= 32768)
            {
                var remote = new StringBuilder(capacity);
                var required = capacity;
                var result = WNetGetConnection(localDrive, remote, ref required);
                if (result == NoError)
                {
                    return Path.Combine(remote.ToString(), fullPath[root.Length..]);
                }

                if (result != ErrorMoreData)
                {
                    break;
                }

                capacity = Math.Max(capacity * 2, required);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return fullPath;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetGetConnectionW", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
