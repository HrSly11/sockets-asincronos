using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatCliente.Media;

public sealed class WaveAudioRecorder : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] ptr, IntPtr hModule, int flags);

    private const int SND_ASYNC = 0x0001;
    private const int SND_MEMORY = 0x0004;

    private bool isRecording;
    private string? currentTempFile;

    public bool IsRecording => isRecording;

    public void StartRecording()
    {
        if (isRecording) return;

        var tempDir = Path.Combine(Path.GetTempPath(), "ChatRedesAudios");
        Directory.CreateDirectory(tempDir);
        currentTempFile = Path.Combine(tempDir, $"voicenote_{Guid.NewGuid():N}.wav");

        mciSendString("close voicemicrophone wait", null, 0, IntPtr.Zero);
        mciSendString("open new type waveaudio alias voicemicrophone wait", null, 0, IntPtr.Zero);
        mciSendString("set voicemicrophone bitspersample 16 samplespersec 16000 channels 1 wait", null, 0, IntPtr.Zero);
        mciSendString("record voicemicrophone", null, 0, IntPtr.Zero);

        isRecording = true;
    }

    public (byte[] AudioBytes, string FilePath) StopRecording()
    {
        if (!isRecording || currentTempFile is null)
        {
            return (Array.Empty<byte>(), string.Empty);
        }

        isRecording = false;

        mciSendString("stop voicemicrophone wait", null, 0, IntPtr.Zero);
        mciSendString($"save voicemicrophone \"{currentTempFile}\" wait", null, 0, IntPtr.Zero);
        mciSendString("close voicemicrophone wait", null, 0, IntPtr.Zero);

        if (File.Exists(currentTempFile))
        {
            var bytes = File.ReadAllBytes(currentTempFile);
            if (bytes.Length > 44)
            {
                return (bytes, currentTempFile);
            }
        }

        return (Array.Empty<byte>(), string.Empty);
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
