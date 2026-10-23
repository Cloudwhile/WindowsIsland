namespace WindowsIsland.Services;

internal static class DiagnosticSaveDialog
{
    public static Task<string?> PickAsync(nint owner, string fileType, DateTime now) =>
        NativeFileDialog.SaveDiagnosticAsync(owner, fileType, now);
}
