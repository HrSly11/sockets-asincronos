using System.Runtime.InteropServices;
using System.Text;

namespace ChatCliente.Media;

public sealed class WaveAudioRecorder : IDisposable
{
    private bool isRecording;
    private string? currentTempFile;

    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] ptr, IntPtr hModule, int flags);

    private const int SND_ASYNC = 0x0001;
    private const int SND_MEMORY = 0x0004;

    public bool IsRecording => isRecording;

    public void StartRecording()
    {
        if (isRecording) return;

        currentTempFile = Path.Combine(Path.GetTempPath(), $"voicenote_{Guid.NewGuid():N}.wav");
        mciSendString("close voicemicrophone", null, 0, IntPtr.Zero);
        mciSendString("open new type waveaudio alias voicemicrophone", null, 0, IntPtr.Zero);
        mciSendString("set voicemicrophone bitspersample 16 samplespersec 16000 channels 1", null, 0, IntPtr.Zero);
        mciSendString("record voicemicrophone", null, 0, IntPtr.Zero);
        isRecording = true;
    }

    public (byte[] AudioBytes, string FilePath) StopRecording()
    {
        if (!isRecording || currentTempFile is null)
        {
            return (Array.Empty<byte>(), string.Empty);
        }

        mciSendString("stop voicemicrophone", null, 0, IntPtr.Zero);
        mciSendString($"save voicemicrophone \"{currentTempFile}\"", null, 0, IntPtr.Zero);
        mciSendString("close voicemicrophone", null, 0, IntPtr.Zero);
        isRecording = false;

        if (File.Exists(currentTempFile))
        {
            var bytes = File.ReadAllBytes(currentTempFile);
            return (bytes, currentTempFile);
        }

        return (Array.Empty<byte>(), string.Empty);
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
            try
            {
                mciSendString("close voicemicrophone", null, 0, IntPtr.Zero);
            }
            catch
            {
            }
        }
    }
}
