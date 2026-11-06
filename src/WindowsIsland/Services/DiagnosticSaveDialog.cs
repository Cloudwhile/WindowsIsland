namespace WindowsIsland.Services;

internal static class DiagnosticSaveDialog
{
    public static Task<string?> PickAsync(nint owner, string fileType, DateTime now, CancellationToken token = default) =>
        NativeFileDialog.SaveDiagnosticAsync(owner, fileType, now, token);
}
