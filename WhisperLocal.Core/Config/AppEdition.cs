using System.Reflection;

namespace WhisperLocal.Config;

/// <summary>
/// Full or Lite, baked in at build time (-p:Edition=Lite). Lite leaves out everything that runs a model on this computer
/// (whisper-server, llama-server and, on macOS, speaker separation), so speech is recognised and translated only with
/// Gemini or Groq, and the windows show only what Lite can do.
/// </summary>
internal static class AppEdition
{
    public static bool IsLite { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "Edition" && string.Equals(a.Value, "Lite", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>"Full" or "Lite", as shown next to the version.</summary>
    public static string Name => IsLite ? "Lite" : "Full";
}
