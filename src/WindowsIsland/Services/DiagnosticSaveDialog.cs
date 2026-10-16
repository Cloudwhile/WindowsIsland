using System.Runtime.InteropServices;

namespace WindowsIsland.Services;

internal static class DiagnosticSaveDialog
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint FileSystemPath = 0x80058000;
    private static readonly Guid ClientGuid = new("76EB5336-191A-42F4-96B7-D3B69DFBBE49");

    public static Task<string?> PickAsync(nint owner, string fileType, DateTime now)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Pick(owner, fileType, now)); }
            catch (Exception error) { completion.TrySetException(error); }
        })
        { IsBackground = true, Name = "WindowsIsland.DiagnosticSaveDialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string? Pick(nint owner, string fileType, DateTime now)
    {
        var dialog = (IFileSaveDialog)new NativeFileSaveDialog();
        try
        {
            ThrowIfFailed(dialog.SetOptions((uint)(FileDialogOptions.NoChangeDirectory | FileDialogOptions.OverwritePrompt
                | FileDialogOptions.PathMustExist | FileDialogOptions.ForceFileSystem | FileDialogOptions.DoNotAddToRecent)));
            var clientGuid = ClientGuid;
            ThrowIfFailed(dialog.SetClientGuid(ref clientGuid));
            ThrowIfFailed(dialog.SetFileTypes(1, [new FilterSpec(fileType + " (*.json)", "*.json")]));
            ThrowIfFailed(dialog.SetDefaultExtension("json"));
            ThrowIfFailed(dialog.SetFileName("WindowsIsland-diagnostics-" + now.ToString("yyyyMMdd-HHmmss") + ".json"));
            var result = dialog.Show(owner);
            if (result == Cancelled) return null;
            ThrowIfFailed(result);
            ThrowIfFailed(dialog.GetResult(out var item));
            try
            {
                ThrowIfFailed(item.GetDisplayName(FileSystemPath, out var path));
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [Flags]
    private enum FileDialogOptions : uint
    {
        OverwritePrompt = 0x2,
        NoChangeDirectory = 0x8,
        ForceFileSystem = 0x40,
        PathMustExist = 0x800,
        DoNotAddToRecent = 0x02000000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct FilterSpec(string name, string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Pattern = pattern;
    }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private class NativeFileSaveDialog;

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint owner);
        [PreserveSig] int SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        [PreserveSig] int SetFileTypeIndex(uint index);
        [PreserveSig] int GetFileTypeIndex(out uint index);
        [PreserveSig] int Advise(nint events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int SetDefaultFolder(nint folder);
        [PreserveSig] int SetFolder(nint folder);
        [PreserveSig] int GetFolder(out nint folder);
        [PreserveSig] int GetCurrentSelection(out nint item);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetFileName(out nint name);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem item);
        [PreserveSig] int AddPlace(nint item, uint alignment);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int result);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(nint filter);
    }

    [ComImport, Guid("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog : IFileDialog
    {
        [PreserveSig] int SetSaveAsItem([MarshalAs(UnmanagedType.Interface)] IShellItem item);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int SetCollectedProperties(nint properties, [MarshalAs(UnmanagedType.Bool)] bool appendDefault);
        [PreserveSig] int GetProperties(out nint properties);
        [PreserveSig] int ApplyProperties([MarshalAs(UnmanagedType.Interface)] IShellItem item, nint progressSink, nint owner, out nint cookie);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handler, ref Guid interfaceId, out nint result);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint type, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem item, uint hint, out int order);
    }
}
