using Microsoft.Extensions.Logging;

namespace CeruleanToolbox.Desktop;

/// <summary>
/// Appends warnings, errors and unhandled exceptions to <c>%LOCALAPPDATA%\CeruleanToolbox\logs\desktop.log</c>. The desktop
/// shell has no console, so without this a Blazor "An unhandled error has occurred" bar leaves no trace to diagnose.
/// Only Warning and above are written, and the file is restarted when it grows past 1 MB.
/// </summary>
internal static class ErrorLog
{
    private static readonly object Gate = new();
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CeruleanToolbox", "logs", "desktop.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // logging must never take the app down
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public sealed class Provider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);

        public void Dispose()
        {
        }

        private sealed class FileLogger : ILogger
        {
            private readonly string _category;

            public FileLogger(string category)
            {
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var text = $"[{logLevel}] {_category}: {formatter(state, exception)}";
                if (exception is not null) text += Environment.NewLine + exception;
                Write(text);
            }
        }
    }
}
