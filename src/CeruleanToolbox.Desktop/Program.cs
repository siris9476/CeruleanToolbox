using CeruleanToolbox;
using CeruleanToolbox.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CeruleanToolbox.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Unhandled exceptions end up in the log file too (the shell has no console to show them).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write("[ThreadException] " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("[UnhandledException] " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => ErrorLog.Write("[UnobservedTaskException] " + e.Exception);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new ErrorLog.Provider()));
        services.AddWindowsFormsBlazorWebView();
        services.AddCeruleanToolboxServices();
        services.AddSingleton<IConnectionFileDialogService, NativeConnectionFileDialogService>();
        services.AddSingleton<IAppLogService, NativeAppLogService>();

        using var serviceProvider = services.BuildServiceProvider();
        using var mainForm = new MainForm(serviceProvider);
        Application.Run(mainForm);
    }
}
