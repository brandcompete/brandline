using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Brandline.Services.Audio;

/// <summary>
/// Mikrofonaufnahme über das in Windows eingebaute „Voice Capture DSP“ (CLSID_CWMAudioAEC).
/// Es nimmt selbst vom Mikrofon auf, zieht das Lautsprechersignal ab (Echounterdrückung),
/// unterdrückt Rauschen und regelt den Pegel. Voraussetzung: auf dem Lautsprechergerät läuft
/// eine Wiedergabe – das stellt <see cref="CallAudio"/> sicher, indem es die Wiedergabe zuerst startet.
/// </summary>
public sealed class EchoCancellingCapture : IMicCapture
{
    private static readonly Guid ClsidVoiceCaptureDsp = new("745057C7-F353-4F2D-A7EE-58434477730E");
    private static readonly Guid PropertySet = new("6f52c567-0360-4bd2-9617-ccbf1421c939");

    // MFPKEY_WMAAECMA_* (Property-IDs im obigen Satz)
    private const int PidSystemMode = 2;
    private const int PidDeviceIndexes = 4;
    private const int PidFeatureMode = 5;
    private const int PidEchoLength = 7;
    private const int PidNoiseSuppression = 8;
    private const int PidAgc = 9;

    private const int SingleChannelAec = 0;
    private const int OutputBufferIncomplete = 0x01000000;
    private const int MaxConsecutiveErrors = 300; // ≈ 3 s

    private readonly int _captureIndex;
    private readonly int _renderIndex;
    private readonly FrameAccumulator _frames;
    private Thread? _thread;
    private volatile bool _running;

    /// <param name="captureDeviceId">Tatsächliche Geräte-ID des Mikrofons.</param>
    /// <param name="renderDeviceId">Tatsächliche Geräte-ID des Lautsprechers, auf dem das Gespräch läuft.</param>
    public EchoCancellingCapture(string captureDeviceId, string renderDeviceId)
    {
        _captureIndex = AudioDevices.ActiveIndexOf(DataFlow.Capture, captureDeviceId);
        _renderIndex = AudioDevices.ActiveIndexOf(DataFlow.Render, renderDeviceId);
        if (_captureIndex < 0 || _renderIndex < 0)
            throw new InvalidOperationException("Audiogerät für die Echounterdrückung nicht gefunden.");
        _frames = new FrameAccumulator(f => FrameReady?.Invoke(f));
    }

    public event Action<short[]>? FrameReady;
    public event Action<string>? Failed;

    /// <summary>Startet die Aufnahme; wirft, wenn das DSP nicht initialisiert werden kann.</summary>
    public void Start()
    {
        var started = new TaskCompletionSource();
        _running = true;
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "AEC-Capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        started.Task.GetAwaiter().GetResult();
    }

    private void Run(TaskCompletionSource started)
    {
        object? com = null;
        IMediaObject? dmo = null;
        var format = IntPtr.Zero;
        using var buffer = new NativeMediaBuffer(IMicCapture.SampleRate * 2); // 1 s Platz

        try
        {
            com = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidVoiceCaptureDsp, throwOnError: true)!)!;
            dmo = (IMediaObject)com;
            var props = (IPropertyStore)com;

            SetInt(props, PidSystemMode, SingleChannelAec);
            SetInt(props, PidDeviceIndexes, (_renderIndex << 16) | (_captureIndex & 0xFFFF));
            SetBool(props, PidFeatureMode, true);
            SetInt(props, PidNoiseSuppression, 1);
            SetBool(props, PidAgc, true);
            SetInt(props, PidEchoLength, 256);

            format = CreatePcmFormat();
            var mediaType = new DmoMediaType
            {
                MajorType = new Guid("73647561-0000-0010-8000-00AA00389B71"), // MEDIATYPE_Audio
                SubType = new Guid("00000001-0000-0010-8000-00AA00389B71"),   // MEDIASUBTYPE_PCM
                FixedSizeSamples = 1,
                SampleSize = 2,
                FormatType = new Guid("05589f81-c356-11ce-bf01-00aa0055595a"), // FORMAT_WaveFormatEx
                FormatSize = 18,
                Format = format,
            };
            Check(dmo.SetOutputType(0, ref mediaType, 0), "SetOutputType");
            Check(dmo.AllocateStreamingResources(), "AllocateStreamingResources");
            started.SetResult();
        }
        catch (Exception ex)
        {
            Cleanup(dmo, com, format);
            started.SetException(ex);
            return;
        }

        var outputs = new DmoOutputDataBuffer[1];
        var errors = 0;
        var pcm = new short[IMicCapture.SampleRate];
        try
        {
            while (_running)
            {
                int status;
                do
                {
                    buffer.Length = 0;
                    outputs[0] = new DmoOutputDataBuffer { Buffer = buffer.Pointer };
                    var hr = dmo.ProcessOutput(0, 1, outputs, out _);
                    status = outputs[0].Status;
                    if (hr < 0)
                    {
                        // Kurz nach dem Start kann die Wiedergabe noch nicht laufen – tolerieren.
                        if (++errors > MaxConsecutiveErrors)
                            throw new COMException("Echounterdrückung liefert keine Daten", hr);
                        break;
                    }
                    errors = 0;
                    if (buffer.Length > 0)
                    {
                        var count = buffer.Length / 2;
                        Marshal.Copy(buffer.Data, pcm, 0, count);
                        _frames.Add(pcm.AsSpan(0, count));
                    }
                } while ((status & OutputBufferIncomplete) != 0 && _running);

                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"Echounterdrückung ausgefallen: {ex.Message}");
        }
        finally
        {
            Cleanup(dmo, com, format);
        }
    }

    private static void Cleanup(IMediaObject? dmo, object? com, IntPtr format)
    {
        try { dmo?.FreeStreamingResources(); } catch { /* ignorieren */ }
        if (com != null) Marshal.ReleaseComObject(com);
        if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }

    // ---------------- COM-Hilfen ----------------

    private static IntPtr CreatePcmFormat()
    {
        // WAVEFORMATEX: 16 kHz, 16 Bit, mono
        var p = Marshal.AllocCoTaskMem(18);
        Marshal.WriteInt16(p, 0, 1);                                   // wFormatTag = PCM
        Marshal.WriteInt16(p, 2, 1);                                   // nChannels
        Marshal.WriteInt32(p, 4, IMicCapture.SampleRate);              // nSamplesPerSec
        Marshal.WriteInt32(p, 8, IMicCapture.SampleRate * 2);          // nAvgBytesPerSec
        Marshal.WriteInt16(p, 12, 2);                                  // nBlockAlign
        Marshal.WriteInt16(p, 14, 16);                                 // wBitsPerSample
        Marshal.WriteInt16(p, 16, 0);                                  // cbSize
        return p;
    }

    private static void SetInt(IPropertyStore props, int pid, int value)
    {
        var key = new PropertyKey { FormatId = PropertySet, PropertyId = pid };
        var v = new PropVariant { VarType = 3 /* VT_I4 */, IntValue = value };
        Check(props.SetValue(ref key, ref v), $"SetValue({pid})");
    }

    private static void SetBool(IPropertyStore props, int pid, bool value)
    {
        var key = new PropertyKey { FormatId = PropertySet, PropertyId = pid };
        var v = new PropVariant { VarType = 11 /* VT_BOOL */, BoolValue = (short)(value ? -1 : 0) };
        Check(props.SetValue(ref key, ref v), $"SetValue({pid})");
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} fehlgeschlagen (0x{hr:X8})", hr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public int IntValue;
        [FieldOffset(8)] public short BoolValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DmoMediaType
    {
        public Guid MajorType;
        public Guid SubType;
        public int FixedSizeSamples;
        public int TemporalCompression;
        public int SampleSize;
        public Guid FormatType;
        public IntPtr Unknown;
        public int FormatSize;
        public IntPtr Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DmoOutputDataBuffer
    {
        public IntPtr Buffer; // IMediaBuffer*
        public int Status;
        public long Timestamp;
        public long TimeLength;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("d8ad0f58-5494-4102-97c5-ec798e59bcf4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaObject
    {
        [PreserveSig] int GetStreamCount(out int inputStreams, out int outputStreams);
        [PreserveSig] int GetInputStreamInfo(int stream, out int flags);
        [PreserveSig] int GetOutputStreamInfo(int stream, out int flags);
        [PreserveSig] int GetInputType(int stream, int typeIndex, IntPtr mediaType);
        [PreserveSig] int GetOutputType(int stream, int typeIndex, IntPtr mediaType);
        [PreserveSig] int SetInputType(int stream, IntPtr mediaType, int flags);
        [PreserveSig] int SetOutputType(int stream, ref DmoMediaType mediaType, int flags);
        [PreserveSig] int GetInputCurrentType(int stream, IntPtr mediaType);
        [PreserveSig] int GetOutputCurrentType(int stream, IntPtr mediaType);
        [PreserveSig] int GetInputSizeInfo(int stream, out int size, out int maxLookahead, out int alignment);
        [PreserveSig] int GetOutputSizeInfo(int stream, out int size, out int alignment);
        [PreserveSig] int GetInputMaxLatency(int stream, out long latency);
        [PreserveSig] int SetInputMaxLatency(int stream, long latency);
        [PreserveSig] int Flush();
        [PreserveSig] int Discontinuity(int stream);
        [PreserveSig] int AllocateStreamingResources();
        [PreserveSig] int FreeStreamingResources();
        [PreserveSig] int GetInputStatus(int stream, out int flags);
        [PreserveSig] int ProcessInput(int stream, IntPtr buffer, int flags, long timestamp, long timeLength);
        // LPArray: ohne Angabe würde .NET das Array als SAFEARRAY übergeben.
        [PreserveSig] int ProcessOutput(int flags, int bufferCount,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DmoOutputDataBuffer[] buffers, out int status);
        [PreserveSig] int Lock(int lockFlag);
    }

    /// <summary>
    /// Vom DSP beschriebener Ausgabepuffer, als handgebautes COM-Objekt (IMediaBuffer) im nicht verwalteten
    /// Speicher. Die automatische COM-Brücke von .NET scheitert hier („Type library is not registered“),
    /// daher Vtable mit UnmanagedCallersOnly-Funktionen. Die Lebensdauer steuern wir selbst; das DSP
    /// leiht sich den Puffer nur während ProcessOutput, daher zählen AddRef/Release nicht.
    /// </summary>
    private sealed unsafe class NativeMediaBuffer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Native
        {
            public IntPtr Vtable;
            public IntPtr Data;
            public int Length;
            public int Capacity;
        }

        private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IidMediaBuffer = new("59eff8b9-938c-4a26-82f2-95cb84cdc837");
        private static readonly IntPtr SharedVtable = CreateVtable();

        private Native* _native;

        public NativeMediaBuffer(int capacity)
        {
            _native = (Native*)NativeMemory.AllocZeroed((nuint)sizeof(Native));
            _native->Vtable = SharedVtable;
            _native->Data = (IntPtr)NativeMemory.Alloc((nuint)capacity);
            _native->Capacity = capacity;
        }

        /// <summary>IMediaBuffer*-Zeiger für DMO_OUTPUT_DATA_BUFFER.</summary>
        public IntPtr Pointer => (IntPtr)_native;
        public IntPtr Data => _native->Data;

        public int Length
        {
            get => _native->Length;
            set => _native->Length = value;
        }

        private static IntPtr CreateVtable()
        {
            var vtable = (IntPtr*)NativeMemory.Alloc((nuint)(sizeof(IntPtr) * 6));
            delegate* unmanaged[Stdcall]<Native*, Guid*, IntPtr*, int> queryInterface = &QueryInterface;
            delegate* unmanaged[Stdcall]<Native*, uint> addRef = &AddRef;
            delegate* unmanaged[Stdcall]<Native*, uint> release = &Release;
            delegate* unmanaged[Stdcall]<Native*, int, int> setLength = &SetLength;
            delegate* unmanaged[Stdcall]<Native*, int*, int> getMaxLength = &GetMaxLength;
            delegate* unmanaged[Stdcall]<Native*, IntPtr*, int*, int> getBufferAndLength = &GetBufferAndLength;
            vtable[0] = (IntPtr)queryInterface;
            vtable[1] = (IntPtr)addRef;
            vtable[2] = (IntPtr)release;
            vtable[3] = (IntPtr)setLength;
            vtable[4] = (IntPtr)getMaxLength;
            vtable[5] = (IntPtr)getBufferAndLength;
            return (IntPtr)vtable;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(Native* self, Guid* iid, IntPtr* result)
        {
            if (*iid == IidUnknown || *iid == IidMediaBuffer)
            {
                *result = (IntPtr)self;
                return 0;
            }
            *result = IntPtr.Zero;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Native* self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(Native* self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetLength(Native* self, int length)
        {
            if (length < 0 || length > self->Capacity) return unchecked((int)0x80070057); // E_INVALIDARG
            self->Length = length;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetMaxLength(Native* self, int* maxLength)
        {
            *maxLength = self->Capacity;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetBufferAndLength(Native* self, IntPtr* buffer, int* length)
        {
            if (buffer != null) *buffer = self->Data;
            if (length != null) *length = self->Length;
            return 0;
        }

        public void Dispose()
        {
            if (_native == null) return;
            NativeMemory.Free((void*)_native->Data);
            NativeMemory.Free(_native);
            _native = null;
        }
    }
}