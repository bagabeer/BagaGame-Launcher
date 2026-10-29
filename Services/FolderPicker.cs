using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BagaGame.Services;

/// <summary>
/// The modern "select a folder" shell dialog. <c>Microsoft.Win32.OpenFolderDialog</c>
/// only exists on .NET 8+, and the WinForms tree dialog looks out of place here,
/// so the Vista IFileOpenDialog is called directly.
/// </summary>
internal static class FolderPicker
{
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint SigdnFilesysPath = 0x80058000;
    private const int Cancelled = unchecked((int)0x800704C7);

    public static string? Pick(Window owner, string title, string? initialDirectory = null)
    {
        var dialog = (IFileDialog)new FileOpenDialog();

        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist);
            dialog.SetTitle(title);

            if (!string.IsNullOrWhiteSpace(initialDirectory) &&
                Directory.Exists(initialDirectory) &&
                SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero, typeof(IShellItem).GUID, out var start) == 0)
            {
                dialog.SetFolder(start);
            }

            var ownerHandle = new WindowInteropHelper(owner).Handle;
            var result = dialog.Show(ownerHandle);

            if (result == Cancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(result);

            dialog.GetResult(out var item);
            item.GetDisplayName(SigdnFilesysPath, out var path);
            return path;
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog;

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // IModalWindow
        [PreserveSig]
        int Show(IntPtr parent);

        // IFileDialog
        void SetFileTypes(uint count, IntPtr filterSpec);

        void SetFileTypeIndex(uint index);

        void GetFileTypeIndex(out uint index);

        void Advise(IntPtr events, out uint cookie);

        void Unadvise(uint cookie);

        void SetOptions(uint options);

        void GetOptions(out uint options);

        void SetDefaultFolder(IShellItem folder);

        void SetFolder(IShellItem folder);

        void GetFolder(out IShellItem folder);

        void GetCurrentSelection(out IShellItem selection);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        void GetResult(out IShellItem item);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid id, out IntPtr result);

        void GetParent(out IShellItem parent);

        void GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string name);

        void GetAttributes(uint mask, out uint attributes);

        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid id,
        out IShellItem item);
}
