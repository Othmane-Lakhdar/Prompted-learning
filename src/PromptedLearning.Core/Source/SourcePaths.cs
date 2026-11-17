using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PromptedLearning.Core;

internal static class SourcePaths
{
    internal static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains(':') ||
            value.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new ArgumentException("Expected a normalized relative logical path.", nameof(value));
        return value;
    }
    internal static string RootName(string value)
    {
        Relative(value);
        if (value.Contains('/')) throw new ArgumentException("Logical root names have one path component.");
        return value;
    }
    internal static bool Within(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
    internal static string Physical(string root, string relative, bool allowReparse)
    {
        root = Path.GetFullPath(root);
        var current = root;
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("reparse_root_not_supported");
        foreach (var component in Relative(relative).Split('/'))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
            if (!allowReparse) throw new IOException("reparse_not_authorized");
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(true) ?? throw new IOException("unresolved_reparse_target");
            current = target.FullName;
            if (!Within(current, root)) throw new IOException("reparse_boundary_escape");
        }
        if (!Within(current, root)) throw new IOException("source_boundary_escape");
        return current;
    }
    internal static string HandlePath(FileStream stream, string fallback)
    {
        if (!OperatingSystem.IsWindows()) return fallback;
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var count = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)capacity, 0);
            if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count < capacity)
            {
                var result = buffer.ToString();
                if (result.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)) return "\\\\" + result[8..];
                return result.StartsWith("\\\\?\\", StringComparison.Ordinal) ? result[4..] : result;
            }
            capacity = checked((int)count + 1);
        }
        throw new IOException("Resolved source path is too long.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
}
