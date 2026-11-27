using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Diskovy
{
    public static class FileActions
    {
        private const FileAttributes BlockedAttributes = FileAttributes.Directory | FileAttributes.System |
            FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.ReadOnly;
        private static readonly string[] ProtectedLocations = GetProtectedLocations();
        private static readonly HashSet<string> RootFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "WindowsApps",
            "WpSystem", "WUDownloadCache", "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI",
            "$Windows.~BT", "$Windows.~WS", "$WinREAgent", "MSOCache", "XboxGames", "PerfLogs"
        };
        private static readonly HashSet<string> SystemFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bootmgr", "bootnxt", "hiberfil.sys", "pagefile.sys", "swapfile.sys", "ntuser.dat", "usrclass.dat",
            "ntldr", "ntdetect.com", "boot.ini", "bootsect.bak", "memory.dmp"
        };
        private static string[] GetProtectedLocations()
        {
            var folders = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData };
            return folders.Select(Environment.GetFolderPath).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        }

        internal static bool Eligible(string path, FileAttributes attributes)
        {
            if ((attributes & BlockedAttributes) != 0 || Paths.RootLength(path) != 3 || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (GetDriveTypeW(path.Substring(0, 3)) != 3) return false; // Shell recycle is enabled only on local fixed drives.
            if (ProtectedLocations.Any(p => Paths.Inside(path, p))) return false;
            var parts = path.Substring(3).Split('\\');
            if (RootFolders.Contains(parts[0]) || SystemFiles.Contains(parts[parts.Length - 1])) return false;
            foreach (string part in parts)
            {
                // Shell path parsing can normalize trailing periods and spaces to a different item.
                if (part.Length == 0 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.Contains(":") || part.Any(char.IsControl)) return false;
                if (part.Equals("AppData", StringComparison.OrdinalIgnoreCase) || part.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return false;
            }
            Assembly entry = Assembly.GetEntryAssembly();
            if (entry != null && !string.IsNullOrEmpty(entry.Location) &&
                (path.Equals(entry.Location, StringComparison.OrdinalIgnoreCase) || path.Equals(entry.Location + ".config", StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }

        public static void Recycle(LargeFile selected)
        {
            if (selected == null) throw new ArgumentNullException("selected");
            if (!selected.CanRecycle) throw new InvalidOperationException("This file is protected.");
            var handles = new Stack<SafeFileHandle>();
            try
            {
                // Hold every parent against rename/replacement while validating and acting. Open the link itself.
                foreach (string path in Paths.Components(selected.Path))
                {
                    bool leaf = path.Length == selected.Path.Length;
                    const uint readAttributes = 0x80, openExisting = 3, openReparse = 0x00200000, backupSemantics = 0x02000000;
                    var handle = CreateFileW(Paths.Extended(path), readAttributes,
                        leaf ? FileShare.Read | FileShare.Delete : FileShare.ReadWrite, IntPtr.Zero,
                        openExisting, openReparse | backupSemantics, IntPtr.Zero);
                    handles.Push(handle);
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    HandleInfo info;
                    if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || (!leaf && (info.Attributes & FileAttributes.Directory) == 0))
                        throw new IOException("The path changed since the scan. Scan again.");
                    if (!leaf) continue;
                    if (!Eligible(selected.Path, info.Attributes) || info.Created.Value != selected.Created || info.Written.Value != selected.Written ||
                        (((long)info.SizeHigh << 32) | info.SizeLow) != selected.Bytes)
                        throw new IOException("The file changed since the scan. Scan again.");
                    // This catches DOS short aliases and other paths resolving inside a protected location.
                    var canonical = new StringBuilder(32768);
                    uint count = GetFinalPathNameByHandleW(handle, canonical, (uint)canonical.Capacity, 0);
                    if (count == 0 || count >= canonical.Capacity || !Paths.Extended(selected.Path).Equals(canonical.ToString(), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Windows could not verify this file's location.");
                }
                ShellRecycle.Move(selected.Path);
            }
            finally { while (handles.Count > 0) handles.Pop().Dispose(); }
        }

        public static void Locate(string path)
        {
            string full = Paths.Normalize(path);
            if (Native.GetFileAttributesW(Paths.Extended(full)) == Native.InvalidAttributes) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attributes;
                Marshal.ThrowExceptionForHR(SHParseDisplayName(full, IntPtr.Zero, out pidl, 0, out attributes));
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0));
            }
            finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HandleInfo
        {
            internal FileAttributes Attributes;
            internal Native.Timestamp Created, Accessed, Written;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetDriveTypeW(string root);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHParseDisplayName(string path, IntPtr binding, out IntPtr item, uint flags, out uint attributes);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
    }
}
