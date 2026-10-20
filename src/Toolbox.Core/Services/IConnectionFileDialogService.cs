namespace Toolbox.Core.Services;

/// <summary>
/// Abstracts how a connection export/import file reaches disk. The web project downloads it
/// through the browser via a minimal API endpoint (Program.cs); the desktop shell has no HTTP
/// pipeline at all (BlazorWebView hosts components in-process, no Kestrel), so it uses native
/// Save/Open dialogs instead. <see cref="UsesNativeFileDialogs"/> tells Connections.razor which
/// UI to render.
/// </summary>
public interface IConnectionFileDialogService
{
    bool UsesNativeFileDialogs { get; }

    Task<bool> SaveFileAsync(byte[] content, string suggestedFileName);

    Task<(byte[] Bytes, string FileName)?> PickFileToImportAsync();
}

/// <summary>
/// Registered by the web project. Connections.razor never actually calls these methods when
/// <see cref="UsesNativeFileDialogs"/> is false — it falls back to its own form-download /
/// InputFile UI instead, which is the only way to reach the browser's filesystem from a page
/// served over HTTP.
/// </summary>
public sealed class BrowserConnectionFileDialogService : IConnectionFileDialogService
{
    public bool UsesNativeFileDialogs => false;

    public Task<bool> SaveFileAsync(byte[] content, string suggestedFileName) =>
        throw new NotSupportedException(T("The web version downloads the file through the browser, not through this service."));

    public Task<(byte[] Bytes, string FileName)?> PickFileToImportAsync() =>
        throw new NotSupportedException(T("The web version uploads the file through the browser, not through this service."));
}
