using CeruleanToolbox.Services;

namespace CeruleanToolbox.Desktop;

/// <summary>Uses native WinForms Save/Open dialogs — the desktop shell has no HTTP pipeline to
/// download/upload through (see IConnectionFileDialogService).</summary>
public sealed class NativeConnectionFileDialogService : IConnectionFileDialogService
{
    public bool UsesNativeFileDialogs => true;

    public Task<bool> SaveFileAsync(byte[] content, string suggestedFileName)
    {
        using var dialog = new SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = T("CeruleanToolbox file (*.ceruleantoolbox)|*.ceruleantoolbox|All files (*.*)|*.*"),
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return Task.FromResult(false);
        }

        File.WriteAllBytes(dialog.FileName, content);
        return Task.FromResult(true);
    }

    public Task<(byte[] Bytes, string FileName)?> PickFileToImportAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = T("CeruleanToolbox file (*.ceruleantoolbox)|*.ceruleantoolbox|All files (*.*)|*.*"),
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return Task.FromResult<(byte[], string)?>(null);
        }

        var bytes = File.ReadAllBytes(dialog.FileName);
        return Task.FromResult<(byte[], string)?>((bytes, Path.GetFileName(dialog.FileName)));
    }
}
