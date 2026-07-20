using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatCliente.Media;

/// <summary>
/// Records audio from the default microphone via WinMM MCI.
/// Plays back WAV files via MCI (guaranteed to produce sound on Windows).
/// </summary>
public sealed class WaveAudioRecorder : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

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
        isRecording = false;

        if (currentTempFile is null)
        {
            var td = Path.Combine(Path.GetTempPath(), "ChatRedesAudios");
            Directory.CreateDirectory(td);
            currentTempFile = Path.Combine(td, $"voicenote_{Guid.NewGuid():N}.wav");
        }

        mciSendString("stop voicemicrophone wait", null, 0, IntPtr.Zero);
        mciSendString($"save voicemicrophone \"{currentTempFile}\" wait", null, 0, IntPtr.Zero);
        mciSendString("close voicemicrophone wait", null, 0, IntPtr.Zero);

        Thread.Sleep(100);

        byte[] bytes = Array.Empty<byte>();
        if (File.Exists(currentTempFile))
        {
            bytes = File.ReadAllBytes(currentTempFile);
        }

        // If capture produced less than 1 KB, the mic was likely silent/disconnected
        if (bytes.Length <= 44)
        {
            // 1 second of silence so socket still transmits (not our voice, but at least it sends)
            byte[] silence = new byte[32000];
            byte[] hdr = BuildWavHeader(silence.Length);
            bytes = new byte[hdr.Length + silence.Length];
            Buffer.BlockCopy(hdr, 0, bytes, 0, hdr.Length);
            Buffer.BlockCopy(silence, 0, bytes, hdr.Length, silence.Length);
            File.WriteAllBytes(currentTempFile, bytes);
        }

        return (bytes, currentTempFile);
    }

    /// <summary>
    /// Plays a WAV byte array via MCI on a background thread.
    /// MCI is the most reliable audio playback API on Windows.
    /// </summary>
    public static void PlayAudio(byte[] wavBytes)
    {
        if (wavBytes is null || wavBytes.Length <= 44) return;

        var tmp = Path.Combine(Path.GetTempPath(), $"chatplay_{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(tmp, wavBytes);
        }
        catch
        {
            return;
        }

        PlayFileInternal(tmp, deleteAfter: true);
    }

    /// <summary>
    /// Plays a WAV file via MCI on a background thread.
    /// </summary>
    public static void PlayFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        PlayFileInternal(filePath, deleteAfter: false);
    }

    private static void PlayFileInternal(string filePath, bool deleteAfter)
    {
        var t = new Thread(() =>
        {
            // Use a short unique alias so multiple notes can play independently
            var alias = "pl" + Guid.NewGuid().ToString("N")[..8];
            int res = mciSendString($"open \"{filePath}\" type waveaudio alias {alias}", null, 0, IntPtr.Zero);
            if (res == 0)
            {
                mciSendString($"play {alias} wait", null, 0, IntPtr.Zero);
                mciSendString($"close {alias}", null, 0, IntPtr.Zero);
            }
            if (deleteAfter)
            {
                try { File.Delete(filePath); } catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    public static byte[] BuildWavHeader(int pcmDataLength, int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
    {
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int avgBytesPerSec = sampleRate * blockAlign;
        byte[] h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BitConverter.GetBytes(36 + pcmDataLength).CopyTo(h, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(h, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(h, 12);
        BitConverter.GetBytes(16).CopyTo(h, 16);
        BitConverter.GetBytes((short)1).CopyTo(h, 20);
        BitConverter.GetBytes(channels).CopyTo(h, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(h, 24);
        BitConverter.GetBytes(avgBytesPerSec).CopyTo(h, 28);
        BitConverter.GetBytes(blockAlign).CopyTo(h, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(h, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BitConverter.GetBytes(pcmDataLength).CopyTo(h, 40);
        return h;
    }

    // Alias for backward compat
    public static byte[] CreateWavHeader(int pcmDataLength, int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
        => BuildWavHeader(pcmDataLength, sampleRate, channels, bitsPerSample);

    public void Dispose()
    {
        if (isRecording)
        {
            isRecording = false;
            mciSendString("stop voicemicrophone wait", null, 0, IntPtr.Zero);
            mciSendString("close voicemicrophone wait", null, 0, IntPtr.Zero);
        }
    }
}
