using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glint.Windows.Platform;

internal sealed class WeTypeHook32Client : IDisposable
{
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private Process? child;
    private long retryAfter;
    private bool disposed;

    internal bool Poll(uint threadId, nint hwnd, uint processId, uint permit)
    {
        lock (gate)
        {
            if (disposed || Environment.TickCount64 < retryAfter) return false;
            try
            {
                return Request(threadId, hwnd, processId, permit) == 0;
            }
            catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException)
            {
                Disconnect();
                retryAfter = Environment.TickCount64 + 1000;
                return false;
            }
        }
    }

    internal void CheckModule()
    {
        // The x86 process must load the DLL before it can send the version and machine.
        if (Request(0, 0, 0, 1) != 5)
            throw new InvalidOperationException("32 位微信输入法桥接未拒绝无效窗口。");
    }

    private uint Request(uint threadId, nint hwnd, uint processId, uint permit)
    {
        var connection = Connect();
        using var deadline = new CancellationTokenSource(250);
        byte[] request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, threadId);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), unchecked((uint)(nuint)hwnd));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), processId);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), permit);
        connection.WriteAsync(request.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
        byte[] result = new byte[4];
        connection.ReadExactlyAsync(result.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
        return BinaryPrimitives.ReadUInt32LittleEndian(result);
    }

    private NamedPipeServerStream Connect()
    {
        if (pipe is not null && child is { HasExited: false }) return pipe;
        Disconnect();
        string path = Path.Combine(AppContext.BaseDirectory, "WeTypeHook32.exe");
        if (!File.Exists(path) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "WeTypeSink32.dll")))
            throw new FileNotFoundException("缺少 32 位微信输入法桥接组件。", path);

        string nonce = Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream("Glint.WeTypeHook32." + nonce, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(nonce);
        child = Process.Start(start) ?? throw new IOException("无法启动 32 位微信输入法桥接。");
        using var deadline = new CancellationTokenSource(1200);
        using var current = Process.GetCurrentProcess();
        pipe.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult();
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid) || clientPid != (uint)child.Id ||
            !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint clientSession) || clientSession != (uint)current.SessionId)
            throw new IOException("无法验证 32 位微信输入法桥接进程。");
        byte[] hello = new byte[8];
        pipe.ReadExactlyAsync(hello.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
        if (BinaryPrimitives.ReadUInt32LittleEndian(hello) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(hello.AsSpan(4)) != 0x014c)
            throw new IOException("32 位微信输入法桥接的版本或位数不匹配。");
        return pipe;
    }

    private void Disconnect()
    {
        pipe?.Dispose();
        pipe = null;
        if (child is null) return;
        try
        {
            // Closing the pipe normally gives the helper EOF, including when Glint exits.
            if (!child.WaitForExit(200)) child.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        finally
        {
            child.Dispose();
            child = null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Disconnect();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
}
