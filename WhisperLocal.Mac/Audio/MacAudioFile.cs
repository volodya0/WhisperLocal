using System.Diagnostics;
using NAudio.Wave;
using WhisperLocal.Audio;

namespace WhisperLocal.Mac.Audio;

internal static class MacAudioFile
{
    /// <summary>Decodes any file Core Audio can read (wav, mp3, m4a, flac, caf) to 16 kHz mono PCM with afconvert.</summary>
    public static byte[] Load(string path)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"whisperlocal-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/afconvert")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[] { "-f", "WAVE", "-d", "LEI16@16000", "-c", "1", path, wav })
                psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi)!;
            var error = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidDataException($"afconvert failed ({p.ExitCode}): {error.Trim()}");

            using var reader = new WaveFileReader(wav);
            using var ms = new MemoryStream();
            reader.CopyTo(ms);
            return AudioConvert.To16kMono(ms.ToArray(), reader.WaveFormat);
        }
        finally
        {
            File.Delete(wav);
        }
    }
}
