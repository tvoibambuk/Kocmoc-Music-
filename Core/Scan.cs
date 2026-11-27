using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Diskovy
{
    public sealed class LargeFile
    {
        public string Path { get; private set; }
        public string Name { get; private set; }
        public string Directory { get; private set; }
        public long Bytes { get; private set; }
        public DateTime LastWriteUtc { get; private set; }
        public bool CanRecycle { get { return FileActions.Eligible(Path, Attributes); } }
        internal FileAttributes Attributes { get; private set; }
        internal long Created { get; private set; }
        internal long Written { get; private set; }

        internal LargeFile(string directory, Native.FindData data)
        {
            Directory = directory;
            Name = data.Name;
            Path = Paths.Join(directory, Name);
            Bytes = data.Bytes;
            Attributes = data.Attributes;
            Created = data.Created.Value;
            Written = data.Written.Value;
            // Some drivers expose a reserved value in place of a valid FILETIME.
            LastWriteUtc = Written >= 0 && Written <= 2650467743999999999L
                ? DateTime.FromFileTimeUtc(Written) : DateTime.MinValue;
        }
    }

    public class ScanTotals
    {
        public long FilesSeen { get; internal set; }
        public long BytesSeen { get; internal set; }
        public long Matches { get; internal set; }
        public long Skipped { get; internal set; }
        public long ReparseSkipped { get; internal set; }
    }

    public sealed class ScanProgress : ScanTotals
    {
        public string CurrentDirectory { get; private set; }
        internal ScanProgress(ScanTotals totals, string directory)
        {
            CurrentDirectory = directory;
            FilesSeen = totals.FilesSeen;
            BytesSeen = totals.BytesSeen;
            Matches = totals.Matches;
            Skipped = totals.Skipped;
            ReparseSkipped = totals.ReparseSkipped;
        }
    }

    public sealed class ScanResult : ScanTotals
    {
        public List<LargeFile> Files { get; internal set; }
        public bool Cancelled { get; internal set; }
        public bool Truncated { get { return Matches > Files.Count; } }
    }

    public static class DiskScanner
    {
        public static string[] FixedRoots()
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name).ToArray();
        }

        // The UI runs this synchronous traversal on a worker. File contents are never opened by the scan.
        public static ScanResult Scan(IEnumerable<string> roots, long minimumBytes, int limit,
            CancellationToken cancellation, Action<ScanProgress> onProgress)
        {
            if (roots == null) throw new ArgumentNullException("roots");
            if (minimumBytes < 0) throw new ArgumentOutOfRangeException("minimumBytes");
            if (limit < 1) throw new ArgumentOutOfRangeException("limit");
            var result = new ScanResult();
            var accepted = new List<string>();
            foreach (string root in roots)
            {
                string full = Paths.Normalize(root);
                if (accepted.Any(p => Paths.Inside(full, p))) continue;
                accepted.RemoveAll(p => Paths.Inside(p, full));
                accepted.Add(full);
            }

            var pending = new Stack<string>();
            foreach (string root in Enumerable.Reverse(accepted))
            {
                if (cancellation.IsCancellationRequested) break;
                if (ValidateRoot(root, result)) pending.Push(root);
            }
            var ranked = new SortedSet<LargeFile>(new SizeComparer());
            var timer = Stopwatch.StartNew();
            string current = pending.Count > 0 ? pending.Peek() : string.Empty;
            if (onProgress != null) onProgress(new ScanProgress(result, current));

            while (pending.Count > 0 && !cancellation.IsCancellationRequested)
            {
                current = pending.Pop();
                // Recheck the directory immediately before enumeration, since a queued folder may have changed.
                uint bits = Native.GetFileAttributesW(Paths.Extended(current));
                if (bits == Native.InvalidAttributes || (bits & (uint)FileAttributes.Directory) == 0) { result.Skipped++; continue; }
                if ((bits & (uint)FileAttributes.ReparsePoint) != 0) { result.ReparseSkipped++; continue; }
                Native.FindData entry;
                using (var handle = Native.FindFirstFileExW(Paths.Extended(Paths.Join(current, "*")), 1,
                    out entry, 0, IntPtr.Zero, 2)) // FindExInfoBasic and FIND_FIRST_EX_LARGE_FETCH
                {
                    if (handle.IsInvalid)
                    {
                        if (Marshal.GetLastWin32Error() != 2) result.Skipped++; // Empty directory is a success.
                        continue;
                    }
                    do
                    {
                        if (cancellation.IsCancellationRequested) break;
                        if (onProgress != null && timer.ElapsedMilliseconds >= 200)
                        {
                            onProgress(new ScanProgress(result, current));
                            timer.Restart();
                        }
                        if (entry.Name == "." || entry.Name == "..") continue;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { result.ReparseSkipped++; continue; }
                        if ((entry.Attributes & FileAttributes.Directory) != 0) { pending.Push(Paths.Join(current, entry.Name)); continue; }
                        result.FilesSeen++;
                        long bytes = entry.Bytes;
                        result.BytesSeen = bytes <= long.MaxValue - result.BytesSeen ? result.BytesSeen + bytes : long.MaxValue;
                        if (bytes < minimumBytes) continue;
                        result.Matches++;
                        if (ranked.Count == limit && bytes < ranked.Min.Bytes) continue;
                        ranked.Add(new LargeFile(current, entry));
                        if (ranked.Count > limit) ranked.Remove(ranked.Min);
                    } while (Native.FindNextFileW(handle, out entry));
                    if (!cancellation.IsCancellationRequested && Marshal.GetLastWin32Error() != 18) result.Skipped++;
                }
            }
            result.Cancelled = cancellation.IsCancellationRequested;
            result.Files = ranked.Reverse().ToList();
            if (onProgress != null) onProgress(new ScanProgress(result, current));
            return result;
        }

        private static bool ValidateRoot(string root, ScanResult result)
        {
            foreach (string path in Paths.Components(root))
            {
                uint bits = Native.GetFileAttributesW(Paths.Extended(path));
                if (bits == Native.InvalidAttributes || (bits & (uint)FileAttributes.Directory) == 0) { result.Skipped++; return false; }
                if ((bits & (uint)FileAttributes.ReparsePoint) != 0) { result.ReparseSkipped++; return false; }
            }
            return true;
        }

        private sealed class SizeComparer : IComparer<LargeFile>
        {
            public int Compare(LargeFile a, LargeFile b)
            {
                int size = a.Bytes.CompareTo(b.Bytes);
                return size != 0 ? size : StringComparer.Ordinal.Compare(b.Path, a.Path);
            }
        }
    }

    internal static class Paths
    {
        internal static string Join(string folder, string name) { return folder.EndsWith("\\", StringComparison.Ordinal) ? folder + name : folder + "\\" + name; }
        internal static string Extended(string path) { return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path; }
        internal static bool Inside(string path, string root)
        {
            root = root.TrimEnd('\\');
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A folder path is required.");
            string path = value.Replace('/', '\\');
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                RootLength(path) == 0 || path.IndexOfAny(new char[] {'\0','*','?','"','<','>','|'}) >= 0 || path.IndexOf(':', 2) >= 0)
                throw new ArgumentException("Select a drive or a folder using its full path.");
            var buffer = new StringBuilder(32768);
            uint count = Native.GetFullPathNameW(path, (uint)buffer.Capacity, buffer, IntPtr.Zero);
            if (count == 0 || count >= buffer.Capacity) throw new IOException("Windows could not resolve this folder.");
            string full = buffer.ToString();
            int rootLength = RootLength(full);
            return full.Length <= rootLength ? full : full.TrimEnd('\\');
        }

        internal static int RootLength(string path)
        {
            if (path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) &&
                path[1] == ':' && path[2] == '\\') return 3;
            if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return 0;
            int serverEnd = path.IndexOf('\\', 2);
            if (serverEnd < 3 || serverEnd + 1 == path.Length || path[serverEnd + 1] == '\\') return 0;
            int shareEnd = path.IndexOf('\\', serverEnd + 1);
            return shareEnd < 0 ? path.Length : shareEnd;
        }

        internal static IEnumerable<string> Components(string full)
        {
            int start = RootLength(full);
            yield return full.Substring(0, start);
            while (start < full.Length)
            {
                if (full[start] == '\\') start++;
                if (start == full.Length) break;
                int end = full.IndexOf('\\', start);
                start = end < 0 ? full.Length : end;
                yield return full.Substring(0, start);
            }
        }
    }

    internal static class Native
    {
        internal const uint InvalidAttributes = 0xffffffff;
        [StructLayout(LayoutKind.Sequential)]
        internal struct Timestamp
        {
            internal uint Low, High;
            internal long Value { get { return ((long)High << 32) | Low; } }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct FindData
        {
            internal FileAttributes Attributes;
            internal Timestamp Created, Accessed, Written;
            internal uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] internal string Alternate;
            internal long Bytes { get { return ((long)SizeHigh << 32) | SizeLow; } }
        }
        internal sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private FindHandle() : base(true) { }
            protected override bool ReleaseHandle() { return FindClose(handle); }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern FindHandle FindFirstFileExW(string pattern, int level, out FindData data, int search, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern bool FindNextFileW(FindHandle handle, out FindData data);
        [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        internal static extern uint GetFullPathNameW(string name, uint count, StringBuilder buffer, IntPtr filePart);
    }
}
