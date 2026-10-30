namespace WhisperLocal.Pipeline;

/// <summary>A voice model: turns a stretch of speech into a voiceprint, so lines can be told apart by who speaks.</summary>
public sealed record VoiceModel(string Title, string File, string Url, string Size, string Sha256, string Hint);

/// <summary>The voice model for telling speakers apart while listening (Listen.SeparateSpeakers).</summary>
public static class VoiceModels
{
    /// <summary>
    /// CAM++ from 3D-Speaker (Apache-2.0), trained on Chinese and English speakers, in the ONNX form sherpa-onnx
    /// publishes; of the voice models tried it told speakers apart best on short stretches of speech.
    /// </summary>
    public static VoiceModel Default { get; } = new("CAM++ (3D-Speaker)", "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
        "28 MB", "aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2",
        "Tells the voices in what the Mac plays apart while listening ([1], [2], …); works for any language.");

    /// <summary>Cosine similarity from which two voiceprints count as the same speaker, for <see cref="Default"/>.</summary>
    public const double DefaultThreshold = 0.6;

    public static VoiceModel? Find(string modelPath) =>
        string.Equals(Path.GetFileName(modelPath), Default.File, StringComparison.OrdinalIgnoreCase) ? Default : null;
}

/// <summary>Computes voiceprints (speaker embeddings) of 16 kHz mono 16-bit PCM; implemented by the macOS app.</summary>
public interface IVoiceprints
{
    /// <summary>The voice model is downloaded: <see cref="Compute"/> can tell speakers apart.</summary>
    bool IsAvailable { get; }

    /// <summary>The voiceprint of <paramref name="pcm"/>, or null when there is no model or too little speech.</summary>
    float[]? Compute(byte[] pcm);
}
