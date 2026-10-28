using NAudio.Wave;

namespace WhisperLocal.Audio;

internal static class AudioFile
{
    /// <summary>Loads any file Media Foundation can decode (wav, mp3, m4a, flac) as 16 kHz mono PCM.</summary>
    public static byte[] Load(string path)
    {
        using var reader = new MediaFoundationReader(path);
        using var ms = new MemoryStream();
        reader.CopyTo(ms);
        return AudioConvert.To16kMono(ms.ToArray(), reader.WaveFormat);
    }
}
