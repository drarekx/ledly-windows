using System.Runtime.InteropServices;

namespace Ledly.Windows.Audio;

/// <summary>
/// What the PC is playing: WASAPI loopback of the default output device, shared mode, polled every
/// 10 ms on its own thread. Emits silence while nothing plays (WASAPI sends no packets then) and
/// follows changes of the default device.
/// </summary>
sealed class WasapiLoopback : IAudioCapture
{
    public event Action<ReadOnlyMemory<float>, int, int>? Samples;

    readonly object _gate = new();
    Thread? _thread;
    CancellationTokenSource? _cancel;

    public bool IsRunning { get { lock (_gate) return _thread is not null; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return;
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            _thread = new Thread(() => Run(cancel.Token)) { Name = "ledly.audio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _thread = null;
            _cancel?.Cancel();
            _cancel = null;
        }
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(2000);
    }

    public void Dispose() => Stop();

    void Run(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Capture(stop);
                failures = 0;
            }
            catch (Exception e)
            {
                failures++;
                if (failures <= 3 || failures % 30 == 0) Log.Write($"audio: {e.Message}");
            }
            // Device changed, unplugged or failed: start again shortly (silence meanwhile).
            for (var i = 0; i < 20 && !stop.IsCancellationRequested; i++)
            {
                EmitSilence(48000, 2, 480);
                Thread.Sleep(50);
            }
        }
    }

    void EmitSilence(int rate, int channels, int frames) =>
        Samples?.Invoke(new float[frames * channels], channels, rate);

    /// <summary>Captures until stopped or until the default device changes / goes away.</summary>
    void Capture(CancellationToken stop)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var format = IntPtr.Zero;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device));
            Check(device.GetId(out var deviceId));
            var iid = typeof(IAudioClient).GUID;
            Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var clientObject));
            client = (IAudioClient)clientObject;
            Check(client.GetMixFormat(out format));
            var fmt = MixFormat.Read(format);
            // 100 ms buffer, shared mode, loopback.
            Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 1_000_000, 0, format, IntPtr.Zero));
            var captureIid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref captureIid, out var captureObject));
            capture = (IAudioCaptureClient)captureObject;
            Check(client.Start());
            Log.Write($"audio: {fmt.Channels} ch, {fmt.Rate} Hz, {fmt.Bits} bit{(fmt.IsFloat ? " float" : "")}");

            var buffer = Array.Empty<float>();
            var lastPacket = Environment.TickCount64;
            var lastDeviceCheck = Environment.TickCount64;
            while (!stop.IsCancellationRequested)
            {
                Thread.Sleep(10);
                var any = false;
                while (true)
                {
                    var hr = capture.GetNextPacketSize(out var packet);
                    if (hr == AUDCLNT_E_DEVICE_INVALIDATED) return;
                    Check(hr);
                    if (packet == 0) break;
                    Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                    try
                    {
                        var count = (int)frames * fmt.Channels;
                        if (buffer.Length != count) buffer = new float[count];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero) Array.Clear(buffer);
                        else fmt.Convert(data, buffer, count);
                    }
                    finally
                    {
                        Check(capture.ReleaseBuffer(frames));
                    }
                    if (frames > 0)
                    {
                        Samples?.Invoke(buffer, fmt.Channels, fmt.Rate);
                        // Listeners may keep the array: use a fresh one next time.
                        buffer = Array.Empty<float>();
                        any = true;
                    }
                }
                var now = Environment.TickCount64;
                if (any)
                {
                    lastPacket = now;
                }
                else if (now - lastPacket >= 50)
                {
                    // Nothing playing: WASAPI is quiet, so the levels must be told to fall.
                    EmitSilence(fmt.Rate, fmt.Channels, fmt.Rate / 50);
                    lastPacket = now;
                }
                if (now - lastDeviceCheck >= 1000)
                {
                    lastDeviceCheck = now;
                    if (DefaultDeviceId(enumerator) != deviceId) return;
                }
            }
            client.Stop();
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Release(capture);
            Release(client);
            Release(device);
            Release(enumerator);
        }
    }

    static string? DefaultDeviceId(IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device) < 0) return null;
        try
        {
            return device.GetId(out var id) >= 0 ? id : null;
        }
        finally
        {
            Release(device);
        }
    }

    static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>The shared-mode mix format (WAVEFORMATEX / WAVEFORMATEXTENSIBLE) and conversion to float.</summary>
    readonly record struct MixFormat(int Channels, int Rate, int Bits, int BlockAlign, bool IsFloat)
    {
        static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
        const ushort WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        public static MixFormat Read(IntPtr p)
        {
            var tag = (ushort)Marshal.ReadInt16(p, 0);
            var channels = (ushort)Marshal.ReadInt16(p, 2);
            var rate = Marshal.ReadInt32(p, 4);
            var blockAlign = (ushort)Marshal.ReadInt16(p, 12);
            var bits = (ushort)Marshal.ReadInt16(p, 14);
            var isFloat = tag == WAVE_FORMAT_IEEE_FLOAT;
            if (tag == WAVE_FORMAT_EXTENSIBLE && (ushort)Marshal.ReadInt16(p, 16) >= 22)
            {
                isFloat = Marshal.PtrToStructure<Guid>(p + 24) == SubtypeFloat;
                // wValidBitsPerSample doesn't change the container size; keep wBitsPerSample.
            }
            return new MixFormat(Math.Max((int)channels, 1), rate, bits, blockAlign, isFloat);
        }

        public unsafe void Convert(IntPtr data, float[] output, int count)
        {
            var bytesPerSample = BlockAlign / Channels;
            var src = (byte*)data;
            if (IsFloat && bytesPerSample == 4)
            {
                new ReadOnlySpan<float>(src, count).CopyTo(output);
                return;
            }
            for (var i = 0; i < count; i++)
            {
                var s = src + i * bytesPerSample;
                output[i] = bytesPerSample switch
                {
                    2 => *(short*)s / 32768f,
                    3 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                    4 when IsFloat => *(float*)s,
                    4 => *(int*)s / 2147483648f,
                    8 when IsFloat => (float)*(double*)s,
                    _ => 0,
                };
            }
        }
    }

    // Core Audio COM interop -------------------------------------------------------------------

    const int CLSCTX_ALL = 0x17;
    const int AUDCLNT_SHAREMODE_SHARED = 0;
    const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
