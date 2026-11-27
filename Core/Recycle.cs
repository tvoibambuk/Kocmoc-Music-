using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Diskovy
{
    // Request a move to the Windows Recycle Bin directly. There is no permanent-delete fallback.
    internal static class ShellRecycle
    {
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out ITransferSource source);
            void GetParent(out IShellItem parent);
        }

        [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITransferSource
        {
            // The first five methods occupy the slots before RecycleItem in the Windows SDK ABI.
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetProperties(IntPtr properties);
            void OpenItem(IShellItem item, uint flags, ref Guid iid, out IntPtr result);
            void MoveItem(IShellItem item, IShellItem parent, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem result);
            [PreserveSig] int RecycleItem(IShellItem item, IShellItem recycleBin, uint flags, out IShellItem result);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
        [DllImport("shell32.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void SHGetKnownFolderItem(ref Guid folder, uint flags, IntPtr token, ref Guid iid, out IShellItem item);

        internal static void Move(string path)
        {
            var iid = typeof(IShellItem).GUID;
            var transferIid = typeof(ITransferSource).GUID;
            var transferHandler = new Guid("D5E346A1-F753-4932-B403-4574800E2498");
            var binId = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            IShellItem item = null, parent = null, bin = null, recycled = null;
            ITransferSource source = null;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
                item.GetParent(out parent);
                parent.BindToHandler(IntPtr.Zero, ref transferHandler, ref transferIid, out source);
                SHGetKnownFolderItem(ref binId, 0, IntPtr.Zero, ref iid, out bin);
                int status = source.RecycleItem(item, bin, 0, out recycled);
                Marshal.ThrowExceptionForHR(status);
                if (recycled == null) throw new IOException("The Recycle Bin did not accept the file.");
            }
            finally
            {
                if (recycled != null) Marshal.ReleaseComObject(recycled);
                if (bin != null) Marshal.ReleaseComObject(bin);
                if (source != null) Marshal.ReleaseComObject(source);
                if (parent != null) Marshal.ReleaseComObject(parent);
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }
    }
}
