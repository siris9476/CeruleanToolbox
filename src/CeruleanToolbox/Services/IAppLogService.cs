namespace CeruleanToolbox.Services;

/// <summary>
/// Gives the UI access to the application's own error log file. Only the desktop shell has one (it has no console to show
/// errors in); the web app does not register this service, so the menu entry that uses it simply does not appear there.
/// </summary>
public interface IAppLogService
{
    string LogFilePath { get; }

    /// <summary>Opens the log file in the default editor, or its folder when nothing has been logged yet.</summary>
    void Open();
}
