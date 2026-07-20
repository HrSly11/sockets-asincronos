using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatCliente.Media;

public sealed class WaveAudioRecorder : IDisposable
{
    private const int WAVE_MAPPER = -1;
    private const int CALLBACK_FUNCTION = 0x00030000;
    private const int MM_WIM_DATA = 0x3BD;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] ptr, IntPtr hModule, int flags);

    private const int SND_ASYNC = 0x0001;
    private const int SND_MEMORY = 0x0004;

    private IntPtr hWaveIn = IntPtr.Zero;
    private WaveInProc? waveInProcDelegate;
    private readonly MemoryStream recordedPcmStream = new();
    private readonly List<(IntPtr HeaderPtr, IntPtr BufferPtr, int Length)> buffers = [];
    private bool isRecording;
    private string? currentTempFile;

    public bool IsRecording => isRecording;

    public void StartRecording()
    {
        if (isRecording) return;

        recordedPcmStream.SetLength(0);
        buffers.Clear();
        currentTempFile = Path.Combine(Path.GetTempPath(), $"voicenote_{Guid.NewGuid():N}.wav");

        WAVEFORMATEX format = new()
        {
            wFormatTag = 1, // PCM
            nChannels = 1, // Mono
            nSamplesPerSec = 16000,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = 32000,
            cbSize = 0
        };

        waveInProcDelegate = WaveInCallback;
        IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(waveInProcDelegate);
        int res = waveInOpen(out hWaveIn, WAVE_MAPPER, ref format, callbackPtr, IntPtr.Zero, CALLBACK_FUNCTION);
        if (res != 0)
        {
            mciSendString("close voicemicrophone", null, 0, IntPtr.Zero);
            mciSendString("open new type waveaudio alias voicemicrophone", null, 0, IntPtr.Zero);
            mciSendString("record voicemicrophone", null, 0, IntPtr.Zero);
            isRecording = true;
            return;
        }

        // Prepare 4 recording buffers of 8192 bytes each
        const int bufferSize = 8192;
        for (int i = 0; i < 4; i++)
        {
            IntPtr bufferPtr = Marshal.AllocHGlobal(bufferSize);
            WAVEHDR header = new()
            {
                lpData = bufferPtr,
                dwBufferLength = bufferSize,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0
            };
            IntPtr headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf(header));
            Marshal.StructureToPtr(header, headerPtr, false);

            waveInPrepareHeader(hWaveIn, headerPtr, Marshal.SizeOf(header));
            waveInAddBuffer(hWaveIn, headerPtr, Marshal.SizeOf(header));
            buffers.Add((headerPtr, bufferPtr, bufferSize));
        }

        waveInStart(hWaveIn);
        isRecording = true;
    }

    private void WaveInCallback(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg == MM_WIM_DATA && isRecording && dwParam1 != IntPtr.Zero)
        {
            WAVEHDR waveHdr = Marshal.PtrToStructure<WAVEHDR>(dwParam1);
            if (waveHdr.dwBytesRecorded > 0)
            {
                byte[] pcmData = new byte[waveHdr.dwBytesRecorded];
                Marshal.Copy(waveHdr.lpData, pcmData, 0, (int)waveHdr.dwBytesRecorded);
                lock (recordedPcmStream)
                {
                    recordedPcmStream.Write(pcmData, 0, pcmData.Length);
                }
            }

            if (isRecording && hWaveIn != IntPtr.Zero)
            {
                waveInAddBuffer(hWaveIn, dwParam1, Marshal.SizeOf<WAVEHDR>());
            }
        }
    }

    public (byte[] AudioBytes, string FilePath) StopRecording()
    {
        if (!isRecording || currentTempFile is null)
        {
            return (Array.Empty<byte>(), string.Empty);
        }

        isRecording = false;

        if (hWaveIn != IntPtr.Zero)
        {
            waveInStop(hWaveIn);
            waveInReset(hWaveIn);
            waveInClose(hWaveIn);
            hWaveIn = IntPtr.Zero;

            foreach (var (headerPtr, bufferPtr, _) in buffers)
            {
                Marshal.FreeHGlobal(bufferPtr);
                Marshal.FreeHGlobal(headerPtr);
            }
            buffers.Clear();
        }
        else
        {
            mciSendString("stop voicemicrophone wait", null, 0, IntPtr.Zero);
            mciSendString($"save voicemicrophone \"{currentTempFile}\" wait", null, 0, IntPtr.Zero);
            mciSendString("close voicemicrophone wait", null, 0, IntPtr.Zero);
        }

        byte[] pcmData;
        lock (recordedPcmStream)
        {
            pcmData = recordedPcmStream.ToArray();
        }

        if (pcmData.Length == 0 && File.Exists(currentTempFile))
        {
            var bytes = File.ReadAllBytes(currentTempFile);
            if (bytes.Length > 0)
            {
                return (bytes, currentTempFile);
            }
        }

        if (pcmData.Length == 0)
        {
            // 1.5 seconds of 16kHz 16-bit Mono PCM audio payload (48,000 bytes)
            pcmData = new byte[48000];
        }

        byte[] wavHeader = CreateWavHeader(pcmData.Length, 16000, 1, 16);
        byte[] fullWav = new byte[wavHeader.Length + pcmData.Length];
        Buffer.BlockCopy(wavHeader, 0, fullWav, 0, wavHeader.Length);
        Buffer.BlockCopy(pcmData, 0, fullWav, wavHeader.Length, pcmData.Length);

        File.WriteAllBytes(currentTempFile, fullWav);
        return (fullWav, currentTempFile);
    }

    public static byte[] CreateWavHeader(int pcmDataLength, int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
    {
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int avgBytesPerSec = sampleRate * blockAlign;
        byte[] header = new byte[44];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BitConverter.GetBytes(36 + pcmDataLength).CopyTo(header, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);

        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BitConverter.GetBytes(16).CopyTo(header, 16);
        BitConverter.GetBytes((short)1).CopyTo(header, 20);
        BitConverter.GetBytes(channels).CopyTo(header, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(header, 24);
        BitConverter.GetBytes(avgBytesPerSec).CopyTo(header, 28);
        BitConverter.GetBytes(blockAlign).CopyTo(header, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(header, 34);

        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BitConverter.GetBytes(pcmDataLength).CopyTo(header, 40);

        return header;
    }

    public static void PlayAudio(byte[] wavBytes)
    {
        if (wavBytes is null || wavBytes.Length == 0) return;
        Task.Run(() =>
        {
            try
            {
                PlaySound(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
            }
            catch
            {
            }
        });
    }

    public void Dispose()
    {
        if (isRecording)
        {
            StopRecording();
        }
        recordedPcmStream.Dispose();
    }
}

public sealed class MciAudioPlayer : IDisposable
{
    private readonly string alias;
    private readonly string filePath;
    private bool isOpen;

    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    public MciAudioPlayer(string filePath)
    {
        this.filePath = filePath;
        alias = $"player_{Guid.NewGuid():N}";
    }

    public bool Open()
    {
        if (isOpen) return true;
        if (!File.Exists(filePath)) return false;

        var cmd = $"open \"{filePath}\" type waveaudio alias {alias}";
        var result = mciSendString(cmd, null, 0, IntPtr.Zero);
        isOpen = (result == 0);
        return isOpen;
    }

    public void Play(int fromMs = 0)
    {
        if (!Open()) return;
        mciSendString($"play {alias} from {fromMs}", null, 0, IntPtr.Zero);
    }

    public void Pause()
    {
        if (!isOpen) return;
        mciSendString($"pause {alias}", null, 0, IntPtr.Zero);
    }

    public void Stop()
    {
        if (!isOpen) return;
        mciSendString($"stop {alias}", null, 0, IntPtr.Zero);
    }

    public int GetDurationMs()
    {
        if (!Open()) return 0;
        var sb = new StringBuilder(128);
        mciSendString($"status {alias} length", sb, 128, IntPtr.Zero);
        return int.TryParse(sb.ToString(), out var len) ? len : 0;
    }

    public int GetPositionMs()
    {
        if (!isOpen) return 0;
        var sb = new StringBuilder(128);
        mciSendString($"status {alias} position", sb, 128, IntPtr.Zero);
        return int.TryParse(sb.ToString(), out var pos) ? pos : 0;
    }

    public bool IsPlaying()
    {
        if (!isOpen) return false;
        var sb = new StringBuilder(128);
        mciSendString($"status {alias} mode", sb, 128, IntPtr.Zero);
        return sb.ToString().Trim().Equals("playing", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (isOpen)
        {
            mciSendString($"close {alias}", null, 0, IntPtr.Zero);
            isOpen = false;
        }
    }
}
