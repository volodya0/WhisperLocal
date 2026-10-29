using SherpaOnnx;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Pipeline;

namespace WhisperLocal.Mac.Audio;

/// <summary>
/// Voiceprints for telling speakers apart while listening, with sherpa-onnx and the voice model of
/// Listen.VoiceModelPath. The model loads with the first voiceprint and stays until <see cref="Unload"/> (listening
/// stops). Any thread; one voiceprint at a time.
/// </summary>
internal sealed class Voiceprints(ConfigManager config) : IVoiceprints, IDisposable
{
    private const int SampleRate = 16000;

    private readonly object _lock = new();
    private SpeakerEmbeddingExtractor? _extractor;
    private string? _loaded; // the model file _extractor was made from
    private bool _disposed; // the app quits: a segment still being finished does not load the model again

    public bool IsAvailable => File.Exists(ModelPath);

    public bool IsLoaded => _extractor != null;

    private string ModelPath => AppPaths.Resolve(config.Current.Listen.VoiceModelPath);

    public float[]? Compute(byte[] pcm)
    {
        var samples = AudioConvert.ToFloats(pcm);
        lock (_lock)
        {
            if (Load() is not { } extractor)
                return null;
            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            stream.InputFinished();
            return extractor.IsReady(stream) ? extractor.Compute(stream) : null;
        }
    }

    /// <summary>The extractor for the configured model, loaded if needed; null when the model is not downloaded.</summary>
    private SpeakerEmbeddingExtractor? Load()
    {
        var model = ModelPath;
        if (_extractor != null && _loaded == model)
            return _extractor;
        UnloadLocked();
        if (_disposed || !File.Exists(model))
            return null;
        _extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = model, NumThreads = 2, Provider = "cpu" });
        _loaded = model;
        Log.Info($"Voice model loaded: {Path.GetFileName(model)}");
        return _extractor;
    }

    public void Unload()
    {
        lock (_lock)
            UnloadLocked();
    }

    private void UnloadLocked()
    {
        if (_extractor == null)
            return;
        _extractor.Dispose();
        _extractor = null;
        _loaded = null;
        Log.Info("Voice model unloaded");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            UnloadLocked();
        }
    }
}
