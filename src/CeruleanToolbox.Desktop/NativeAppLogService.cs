using System.Diagnostics;
using CeruleanToolbox.Services;

namespace CeruleanToolbox.Desktop;

/// <summary>Opens the desktop error log (see <see cref="ErrorLog"/>) with the shell's default handler.</summary>
public sealed class NativeAppLogService : IAppLogService
{
    public string LogFilePath => ErrorLog.FilePath;

    public void Open()
    {
        var folder = Path.GetDirectoryName(LogFilePath)!;
        Directory.CreateDirectory(folder);

        // Nothing logged yet (the file only appears at the first warning or error): show the folder instead.
        var target = File.Exists(LogFilePath) ? LogFilePath : folder;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
