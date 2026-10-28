namespace WhisperLocal.Stt;

/// <summary>A ggml Whisper model the main window offers; downloaded from <see cref="ModelNotFoundException.DownloadUrlBase"/>.</summary>
public sealed record WhisperModel(string Title, string File, string Size, string Hint);

public static class WhisperModels
{
    public static IReadOnlyList<WhisperModel> Catalog { get; } =
    [
        new("Large v3 (most accurate)", "ggml-large-v3.bin", "3.1 GB",
            "The most accurate, especially for Ukrainian; about 4 GB of memory while loaded."),
        new("Large v3 Turbo (faster)", "ggml-large-v3-turbo.bin", "1.6 GB",
            "Several times faster with almost the same accuracy; about 2 GB of memory. Best on 8–16 GB Macs."),
    ];

    /// <summary>The catalog entry for a model path (matched by file name), or null for a model added by hand.</summary>
    public static WhisperModel? Find(string modelPath) =>
        Catalog.FirstOrDefault(m => string.Equals(m.File, Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase));
}
