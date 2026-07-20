using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatCliente.Media;

/// <summary>
/// Records audio from the default microphone using the waveIn API (native Windows audio capture).
/// Plays back WAV files via MCI.
/// </summary>
public sealed class WaveAudioRecorder : IDisposable
{
    // ──────────────────────────────────────────────────────────────────────────
    // waveIn P/Invoke
    // ──────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;       // 1 = PCM
        public ushort nChannels;
        public uint   nSamplesPerSec;
        public uint   nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint   dwBufferLength;
        public uint   dwBytesRecorded;
        public IntPtr dwUser;
        public uint   dwFlags;
        public uint   dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private const int WAVE_MAPPER    = -1;
    private const int CALLBACK_NULL  = 0x00000000;
    private const int WHDR_DONE      = 0x00000001;

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

    // MCI — used only for playback
    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    // ──────────────────────────────────────────────────────────────────────────
    // Recording constants
    // ──────────────────────────────────────────────────────────────────────────

    private const int SampleRate     = 8000;
    private const ushort Channels    = 1;
    private const ushort Bits        = 16;
    private const int BytesPerSample = Channels * Bits / 8;
    private const int MaxSeconds     = 30;  // 30-second cap (keeps payload well under 65535 bytes)

    // ──────────────────────────────────────────────────────────────────────────
    // State
    // ──────────────────────────────────────────────────────────────────────────

    private IntPtr  hWaveIn      = IntPtr.Zero;
    private IntPtr  hdrPtr       = IntPtr.Zero;  // unmanaged WAVEHDR
    private GCHandle bufferPin;                   // pinned capture buffer
    private byte[]? captureBuffer;
    private bool    isRecording;
    private string? currentTempFile;

    public bool IsRecording => isRecording;

    // ──────────────────────────────────────────────────────────────────────────
    // Recording
    // ──────────────────────────────────────────────────────────────────────────

    public void StartRecording()
    {
        if (isRecording) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "ChatRedesAudios");
        Directory.CreateDirectory(tempDir);
        currentTempFile = Path.Combine(tempDir, $"voicenote_{Guid.NewGuid():N}.wav");

        var wfx = new WAVEFORMATEX
        {
            wFormatTag      = 1,
            nChannels       = Channels,
            nSamplesPerSec  = SampleRate,
            nAvgBytesPerSec = (uint)(SampleRate * BytesPerSample),
            nBlockAlign     = BytesPerSample,
            wBitsPerSample  = Bits,
            cbSize          = 0
        };

        int res = waveInOpen(out hWaveIn, WAVE_MAPPER, ref wfx, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (res != 0)
        {
            // waveIn not available — fall back (device busy / no mic)
            hWaveIn = IntPtr.Zero;
            isRecording = true;
            return;
        }

        int bufSize = SampleRate * BytesPerSample * MaxSeconds;
        captureBuffer = new byte[bufSize];
        bufferPin = GCHandle.Alloc(captureBuffer, GCHandleType.Pinned);

        // Allocate WAVEHDR in unmanaged heap so the driver can write to it safely
        hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
        Marshal.StructureToPtr(new WAVEHDR
        {
            lpData          = bufferPin.AddrOfPinnedObject(),
            dwBufferLength  = (uint)bufSize,
            dwBytesRecorded = 0,
            dwFlags         = 0
        }, hdrPtr, false);

        waveInPrepareHeader(hWaveIn, hdrPtr, Marshal.SizeOf<WAVEHDR>());
        waveInAddBuffer(hWaveIn, hdrPtr, Marshal.SizeOf<WAVEHDR>());
        waveInStart(hWaveIn);

        isRecording = true;
    }

    public (byte[] AudioBytes, string FilePath) StopRecording()
    {
        isRecording = false;

        if (currentTempFile is null)
        {
            var td = Path.Combine(Path.GetTempPath(), "ChatRedesAudios");
            Directory.CreateDirectory(td);
            currentTempFile = Path.Combine(td, $"voicenote_{Guid.NewGuid():N}.wav");
        }

        byte[] pcmData = Array.Empty<byte>();

        if (hWaveIn != IntPtr.Zero)
        {
            waveInStop(hWaveIn);
            waveInReset(hWaveIn);      // forces driver to mark buffer DONE
            Thread.Sleep(80);          // let driver flush dwBytesRecorded

            var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtr);
            int recorded = (int)hdr.dwBytesRecorded;

            waveInUnprepareHeader(hWaveIn, hdrPtr, Marshal.SizeOf<WAVEHDR>());
            waveInClose(hWaveIn);
            hWaveIn = IntPtr.Zero;

            Marshal.FreeHGlobal(hdrPtr);
            hdrPtr = IntPtr.Zero;

            if (recorded > 0 && captureBuffer is not null)
            {
                int safeLen = Math.Min(recorded, 60000);
                pcmData = new byte[safeLen];
                Buffer.BlockCopy(captureBuffer, 0, pcmData, 0, safeLen);
            }

            bufferPin.Free();
            captureBuffer = null;
        }

        if (pcmData.Length == 0)
        {
            // No mic capture — send 1 s of silence so the socket at least transmits
            pcmData = new byte[SampleRate * BytesPerSample];
        }

        byte[] header = BuildWavHeader(pcmData.Length);
        byte[] wav    = new byte[header.Length + pcmData.Length];
        Buffer.BlockCopy(header, 0, wav, 0, header.Length);
        Buffer.BlockCopy(pcmData, 0, wav, header.Length, pcmData.Length);

        File.WriteAllBytes(currentTempFile, wav);
        return (wav, currentTempFile);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Playback via MCI (proven to produce sound on Windows)
    // ──────────────────────────────────────────────────────────────────────────

    public static void PlayAudio(byte[] wavBytes)
    {
        if (wavBytes is null || wavBytes.Length <= 44) return;

        var tmp = Path.Combine(Path.GetTempPath(), $"chatplay_{Guid.NewGuid():N}.wav");
        try { File.WriteAllBytes(tmp, wavBytes); }
        catch { return; }

        PlayFileInternal(tmp, deleteAfter: true);
    }

    public static void PlayFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        PlayFileInternal(filePath, deleteAfter: false);
    }

    private static void PlayFileInternal(string path, bool deleteAfter)
    {
        var t = new Thread(() =>
        {
            var alias = "p" + Guid.NewGuid().ToString("N")[..8];
            int r = mciSendString($"open \"{path}\" type waveaudio alias {alias}", null, 0, IntPtr.Zero);
            if (r == 0)
            {
                mciSendString($"play {alias} wait", null, 0, IntPtr.Zero);
                mciSendString($"close {alias}", null, 0, IntPtr.Zero);
            }
            if (deleteAfter)
            {
                try { File.Delete(path); } catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WAV header helpers
    // ──────────────────────────────────────────────────────────────────────────

    public static byte[] BuildWavHeader(int pcmLen, int sampleRate = SampleRate, short ch = 1, short bps = 16)
    {
        short  align  = (short)(ch * (bps / 8));
        int    bpsSec = sampleRate * align;
        byte[] h      = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BitConverter.GetBytes(36 + pcmLen).CopyTo(h, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(h, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(h, 12);
        BitConverter.GetBytes(16).CopyTo(h, 16);
        BitConverter.GetBytes((short)1).CopyTo(h, 20);
        BitConverter.GetBytes(ch).CopyTo(h, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(h, 24);
        BitConverter.GetBytes(bpsSec).CopyTo(h, 28);
        BitConverter.GetBytes(align).CopyTo(h, 32);
        BitConverter.GetBytes(bps).CopyTo(h, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BitConverter.GetBytes(pcmLen).CopyTo(h, 40);
        return h;
    }

    public static byte[] CreateWavHeader(int pcmLen, int sampleRate = SampleRate, short channels = 1, short bitsPerSample = 16)
        => BuildWavHeader(pcmLen, sampleRate, channels, bitsPerSample);

    // ──────────────────────────────────────────────────────────────────────────
    // Dispose
    // ──────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (isRecording)
        {
            isRecording = false;
            if (hWaveIn != IntPtr.Zero)
            {
                waveInStop(hWaveIn);
                waveInReset(hWaveIn);
                if (hdrPtr != IntPtr.Zero)
                {
                    waveInUnprepareHeader(hWaveIn, hdrPtr, Marshal.SizeOf<WAVEHDR>());
                    Marshal.FreeHGlobal(hdrPtr);
                    hdrPtr = IntPtr.Zero;
                }
                waveInClose(hWaveIn);
                hWaveIn = IntPtr.Zero;
            }
            if (bufferPin.IsAllocated) bufferPin.Free();
            captureBuffer = null;
        }
    }
}
