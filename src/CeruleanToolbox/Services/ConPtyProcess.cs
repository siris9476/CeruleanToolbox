using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CeruleanToolbox.Services;

/// <summary>
/// A child process attached to a Windows pseudo console (ConPTY): it sees a real terminal, so prompts, colors,
/// line editing and interactive programs work. Input is written as raw terminal bytes, output is the raw VT stream.
/// </summary>
internal sealed class ConPtyProcess : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;

    private IntPtr _pseudoConsole;
    private IntPtr _attributeList;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private SafeProcessHandle? _processHandle;
    private FileStream? _input;
    private FileStream? _output;
    private bool _disposed;

    public int ProcessId { get; private set; }

    public Stream Input => _input!;
    public Stream Output => _output!;

    public static ConPtyProcess Start(string commandLine, short columns, short rows, IDictionary<string, string> environment)
    {
        var process = new ConPtyProcess();
        try
        {
            process.StartCore(commandLine, columns, rows, environment);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private void StartCore(string commandLine, short columns, short rows, IDictionary<string, string> environment)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        _inputWrite = inputWrite;
        _outputRead = outputRead;

        var hr = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole);
        // The pseudo console holds its own references to its ends of the pipes.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0) throw new Win32Exception(hr, T("CreatePseudoConsole is not available (Windows 10 1809 or later is required)."));

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size)) throw new Win32Exception();
        if (!UpdateProcThreadAttribute(_attributeList, 0, ProcThreadAttributePseudoConsole, _pseudoConsole,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

        // STARTF_USESTDHANDLES with null handles makes the child bind its stdio to the pseudo console. Without it,
        // a parent whose own stdio is redirected (a service, `dotnet run` with piped output) leaks those handles to
        // the child: it then prints to the parent's stdout and reads EOF from its stdin, and exits immediately.
        var startupInfo = new StartupInfoEx
        {
            StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = StartfUseStdHandles },
            lpAttributeList = _attributeList,
        };

        var environmentBlock = BuildEnvironmentBlock(environment);
        var environmentPtr = Marshal.StringToHGlobalUni(environmentBlock);
        try
        {
            var buffer = new StringBuilder(commandLine);
            if (!CreateProcess(null, buffer, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environmentPtr,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref startupInfo, out var info))
            {
                throw new Win32Exception();
            }

            ProcessId = info.dwProcessId;
            _processHandle = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            CloseHandle(info.hThread);
        }
        finally
        {
            Marshal.FreeHGlobal(environmentPtr);
        }

        _input = new FileStream(_inputWrite, FileAccess.Write, 4096, isAsync: false);
        _output = new FileStream(_outputRead, FileAccess.Read, 4096, isAsync: false);
    }

    public void Resize(short columns, short rows)
    {
        var console = _pseudoConsole;
        if (console != IntPtr.Zero && !_disposed) ResizePseudoConsole(console, new Coord(columns, rows));
    }

    private const int StillActive = 259;

    /// <summary>The process exit code, or null while it is still running.</summary>
    public int? ExitCode
    {
        get
        {
            if (_processHandle is null || _processHandle.IsInvalid) return -1;
            if (!GetExitCodeProcess(_processHandle.DangerousGetHandle(), out var code)) return -2;
            return code == StillActive ? null : code;
        }
    }

    public bool HasExited => ExitCode is not null;

    public void Kill()
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // already gone
        }
    }

    // Windows wants the whole block as "K=V\0K=V\0\0": inherit the current environment and apply the overrides.
    private static string BuildEnvironmentBlock(IDictionary<string, string> overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            merged[(string)entry.Key] = (string?)entry.Value ?? "";
        }

        foreach (var pair in overrides) merged[pair.Key] = pair.Value;

        var block = new StringBuilder();
        foreach (var pair in merged) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        return block.Append('\0').ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Kill();

        // Closing the pseudo console ends the output pipe, which lets a reader thread finish. It can block
        // while output is still being drained, so it runs off the caller's thread.
        var console = _pseudoConsole;
        _pseudoConsole = IntPtr.Zero;
        var attributeList = _attributeList;
        _attributeList = IntPtr.Zero;
        var input = _input;
        var processHandle = _processHandle;
        Task.Run(() =>
        {
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            input?.Dispose();
            processHandle?.Dispose();
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public readonly short X;
        public readonly short Y;

        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out int exitCode);
}
