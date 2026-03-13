using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static RepoOPS.Services.ConPtyNative;

namespace RepoOPS.Services;

/// <summary>
/// Managed wrapper around a single Windows ConPTY pseudo-console session.
/// Owns the pseudo-console handle, process handle, and I/O streams.
/// </summary>
internal sealed class PtySession : IDisposable
{
    public string SessionId { get; }
    public FileStream OutputStream { get; }
    public FileStream InputStream { get; }
    public int ProcessId { get; }

    private IntPtr _hPC;
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private bool _disposed;

    private PtySession(string sessionId, IntPtr hPC, IntPtr hProcess, IntPtr hThread,
        int processId, FileStream inputStream, FileStream outputStream)
    {
        SessionId = sessionId;
        _hPC = hPC;
        _hProcess = hProcess;
        _hThread = hThread;
        ProcessId = processId;
        InputStream = inputStream;
        OutputStream = outputStream;
    }

    public static PtySession Create(string commandLine, string? workingDirectory, short cols, short rows)
    {
        var size = new COORD { X = cols, Y = rows };

        // Create the two pipe pairs
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!CreatePipe(out var inputRead, out var inputWrite, ref sa, 0))
            throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");

        if (!CreatePipe(out var outputRead, out var outputWrite, ref sa, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");
        }

        // Create pseudo console
        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hPC);
        if (hr != 0)
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        }

        // ConPTY duplicated these handles; close our copies
        inputRead.Dispose();
        outputWrite.Dispose();

        // Build STARTUPINFOEX with the pseudo console attribute
        var lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        var attrList = Marshal.AllocHGlobal(lpSize);

        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize))
                throw new InvalidOperationException(
                    $"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            if (!UpdateProcThreadAttribute(attrList, 0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException(
                    $"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attrList
            };

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory, ref si, out var pi))
            {
                throw new InvalidOperationException(
                    $"CreateProcessW failed: {Marshal.GetLastWin32Error()}");
            }

            // Wrap pipe handles into FileStreams for async I/O
            var inputStream = new FileStream(inputWrite, FileAccess.Write, bufferSize: 256);
            var outputStream = new FileStream(outputRead, FileAccess.Read, bufferSize: 4096);

            var sessionId = $"pty_{pi.dwProcessId}_{DateTime.UtcNow:yyyyMMddHHmmssff}";

            return new PtySession(sessionId, hPC, pi.hProcess, pi.hThread,
                pi.dwProcessId, inputStream, outputStream);
        }
        catch
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            ClosePseudoConsole(hPC);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
    }

    public int? GetExitCode()
    {
        if (_hProcess == IntPtr.Zero) return null;
        if (!GetExitCodeProcess(_hProcess, out var code)) return null;
        return code == STILL_ACTIVE ? null : (int)code;
    }

    public void WaitForExit(int timeoutMs = 5000)
    {
        if (_hProcess != IntPtr.Zero)
            WaitForSingleObject(_hProcess, (uint)timeoutMs);
    }

    /// <summary>
    /// Close the pseudo console handle to force EOF on the output pipe.
    /// Used by the process-exit watcher when the child has exited but ConPTY hasn't closed the pipe.
    /// </summary>
    public void ClosePseudoConsoleHandle()
    {
        if (!_disposed && _hPC != IntPtr.Zero)
        {
            ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Close pseudo console first — this will signal EOF on the output pipe
        if (_hPC != IntPtr.Zero)
        {
            ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        // Wait briefly for process to exit
        WaitForExit();

        InputStream.Dispose();
        OutputStream.Dispose();

        if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }
        if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
    }
}
