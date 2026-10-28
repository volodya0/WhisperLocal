namespace WhisperLocal.Stt;

public interface ISpeechToText : IDisposable
{
    bool IsLoaded { get; }
    DateTime LastUsedUtc { get; }

    /// <summary>Loads the model (starts the server) if needed.</summary>
    Task WarmUpAsync(CancellationToken ct);

    /// <summary>Transcribes a 16 kHz mono 16-bit WAV file (with header).</summary>
    /// <param name="language">Overrides Whisper.Language ("auto", "en", "uk,en"); null uses the config.</param>
    /// <param name="prompt">Overrides Whisper.InitialPrompt (e.g. the text before this audio); null uses the config.</param>
    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct, string? language = null, string? prompt = null);

    /// <summary>Releases the model (and its VRAM).</summary>
    void Unload();
}
