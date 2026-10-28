namespace WhisperLocal.Llm;

/// <summary>A GGUF model the main window offers for download; <paramref name="Url"/> is a direct HuggingFace link.</summary>
public sealed record LlmModel(string Title, string File, string Url, string Size, string Hint);

/// <summary>Models for the live hints (bundled llama-server). Instruction-tuned, ungated, Q4_K_M quantization.</summary>
public static class LlmModels
{
    public static IReadOnlyList<LlmModel> Catalog { get; } =
    [
        new("Gemma 3 4B (recommended)", "gemma-3-4b-it-Q4_K_M.gguf",
            "https://huggingface.co/ggml-org/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf",
            "2.5 GB", "Fast enough to keep up while you speak; good English, decent Ukrainian. About 3 GB of memory while loaded."),
        new("Gemma 3 12B", "gemma-3-12b-it-Q4_K_M.gguf",
            "https://huggingface.co/ggml-org/gemma-3-12b-it-GGUF/resolve/main/gemma-3-12b-it-Q4_K_M.gguf",
            "7.3 GB", "Better hints and Ukrainian, but slower: fits \"When I pause\" better than \"Always\". Needs 16 GB of memory or more."),
        new("Qwen 3 4B", "Qwen3-4B-Q4_K_M.gguf",
            "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
            "2.5 GB", "Fast; strong on technical English, weaker Ukrainian than Gemma."),
    ];

    /// <summary>The catalog entry for a model path (matched by file name), or null for a model added by hand.</summary>
    public static LlmModel? Find(string modelPath) =>
        Catalog.FirstOrDefault(m => string.Equals(m.File, Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase));
}
