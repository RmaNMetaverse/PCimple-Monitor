using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PCimple.Monitoring;

namespace PCimple.Sensors;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args is ["--console"])
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            RunAsync(stop.Token).GetAwaiter().GetResult();
            return;
        }
        ServiceBase.Run(new SensorService());
    }

    private static async Task RunAsync(CancellationToken stop, TaskCompletionSource? started = null)
    {
        using var cpus = new Cpus();
        var cpu = new Poller(cpus.Read, TimeSpan.FromSeconds(2));
        var disks = new Poller(Drives.Read, TimeSpan.FromSeconds(15));
        using var pipe = OpenPipe();
        started?.SetResult();

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stop);
                try
                {
                    var snapshot = new Snapshot(Snapshot.ProtocolVersion, cpu.Get(), disks.Get());
                    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                    if (payload.Length <= Snapshot.MaxBytes)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        deadline.CancelAfter(TimeSpan.FromSeconds(2));
                        byte[] frame = new byte[4 + payload.Length];
                        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
                        payload.CopyTo(frame, 4);
                        await pipe.WriteAsync(frame, deadline.Token);
                        await pipe.FlushAsync(deadline.Token);
                        // DisconnectNamedPipe discards unread data. The client reads a length-prefixed
                        // response and closes first; wait for the kernel to drain this bounded write.
                        await Task.Run(pipe.WaitForPipeDrain, stop).WaitAsync(deadline.Token);
                    }
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                finally
                {
                    if (pipe.IsConnected)
                        pipe.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(cpu.Completion, disks.Completion);
        }
    }

    private static NamedPipeServerStream OpenPipe()
    {
        // Local interactive users have read access. Only LocalSystem can create the first instance.
        string acl = Environment.UserInteractive ? "D:P(A;;GA;;;OW)(A;;GA;;;SY)(A;;GR;;;IU)" : "D:P(A;;GA;;;SY)(A;;GR;;;IU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(acl, 1, out nint descriptor, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var attributes = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            const uint openMode = 2 | 0x40000000 | 0x00080000; // Outbound, overlapped, first instance.
            const uint pipeMode = 8; // Reject remote clients.
            var handle = CreateNamedPipeW(@"\\.\pipe\" + Snapshot.PipeName, openMode, pipeMode, 1,
                Snapshot.MaxBytes, 0, 0, ref attributes);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            try { return new NamedPipeServerStream(PipeDirection.Out, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    private sealed class Poller(Func<Reading[]> read, TimeSpan interval)
    {
        private DateTimeOffset _next;
        private Reading[]? _current;
        public Task Completion { get; private set; } = Task.CompletedTask;

        public Reading[]? Get()
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= _next && Completion.IsCompleted)
            {
                _next = now + interval;
                Completion = Task.Run(() =>
                {
                    try { Volatile.Write(ref _current, read()); }
                    catch (Exception) { /* Retain timestamps so clients can age out the last successful poll. */ }
                });
            }
            return Volatile.Read(ref _current);
        }
    }

    private sealed class SensorService : ServiceBase
    {
        private CancellationTokenSource? _stop;
        private Task? _worker;

        public SensorService()
        {
            ServiceName = "PCimpleMonitorSensors";
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            _stop = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _worker = Task.Run(async () =>
            {
                try { await RunAsync(_stop.Token, started); }
                catch (Exception ex)
                {
                    started.TrySetException(ex);
                    throw;
                }
            });
            started.Task.GetAwaiter().GetResult();
            _ = _worker.ContinueWith(_ => Environment.Exit(1), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        protected override void OnStop()
        {
            _stop?.Cancel();
            _worker?.Wait(TimeSpan.FromSeconds(3));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public nint Descriptor;
        public int Inherit;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string acl, uint revision, out nint descriptor, nint size);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint mode, uint pipeMode, uint instances,
        uint outSize, uint inSize, uint timeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
