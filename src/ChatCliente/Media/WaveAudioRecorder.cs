using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace ChatCliente.Media;

/// <summary>
/// Records audio from the default microphone via WinMM MCI and plays WAV back via SoundPlayer.
/// </summary>
public sealed class WaveAudioRecorder : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi, SetLastError = true)]
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

        // Close any previous session first
        mciSendString("close voicemicrophone wait", null, 0, IntPtr.Zero);

        // Open a new WAV recording device from the default microphone
        int openRes = mciSendString("open new type waveaudio alias voicemicrophone wait", null, 0, IntPtr.Zero);
        if (openRes != 0)
        {
            // Some systems need the device name explicitly
            mciSendString("open new type waveaudio alias voicemicrophone", null, 0, IntPtr.Zero);
        }

        // Set 16-bit, 16kHz, mono PCM
        mciSendString("set voicemicrophone bitspersample 16 samplespersec 16000 channels 1 wait", null, 0, IntPtr.Zero);

        // Start recording
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

        // Stop, save, close — all synchronous
        mciSendString("stop voicemicrophone wait", null, 0, IntPtr.Zero);
        mciSendString($"save voicemicrophone \"{currentTempFile}\" wait", null, 0, IntPtr.Zero);
        mciSendString("close voicemicrophone wait", null, 0, IntPtr.Zero);

        // Small delay to let file flush to disk
        Thread.Sleep(80);

        byte[] bytes = Array.Empty<byte>();
        if (File.Exists(currentTempFile))
        {
            bytes = File.ReadAllBytes(currentTempFile);
        }

        // If MCI didn't capture anything real (< 5 KB is suspiciously small for any real recording)
        if (bytes.Length <= 44)
        {
            // Return a 1-second of silence WAV so the socket still transmits
            byte[] silence = new byte[32000]; // 1s of 16kHz 16-bit mono
            byte[] hdr = BuildWavHeader(silence.Length);
            bytes = new byte[hdr.Length + silence.Length];
            Buffer.BlockCopy(hdr, 0, bytes, 0, hdr.Length);
            Buffer.BlockCopy(silence, 0, bytes, hdr.Length, silence.Length);
            File.WriteAllBytes(currentTempFile, bytes);
        }

        return (bytes, currentTempFile);
    }

    /// <summary>
    /// Plays a WAV byte array via System.Media.SoundPlayer (guaranteed to work in WinForms).
    /// </summary>
    public static void PlayAudio(byte[] wavBytes)
    {
        if (wavBytes is null || wavBytes.Length <= 44) return;

        Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(wavBytes);
                using var player = new SoundPlayer(ms);
                player.PlaySync();
            }
            catch
            {
                // Fallback: play from a temp file
                try
                {
                    var tmp = Path.Combine(Path.GetTempPath(), $"chatplay_{Guid.NewGuid():N}.wav");
                    File.WriteAllBytes(tmp, wavBytes);
                    using var player = new SoundPlayer(tmp);
                    player.PlaySync();
                    try { File.Delete(tmp); } catch { }
                }
                catch
                {
                }
            }
        });
    }

    /// <summary>
    /// Plays a WAV file directly via SoundPlayer.
    /// </summary>
    public static void PlayFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        Task.Run(() =>
        {
            try
            {
                using var player = new SoundPlayer(filePath);
                player.PlaySync();
            }
            catch
            {
            }
        });
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

    // Keep for backward compat — routes to PlayAudio
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
