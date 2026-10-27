namespace WhisperLocal.Config;

/// <summary>
/// Windows: portable layout next to the exe: config.json (copied from the project on build), logs\,
/// whisper\ (bundled runtime) and models\ (downloaded separately, not in source control).
/// macOS: the runtime (whisper/) stays next to the executable (inside the .app bundle), while config.json,
/// logs/ and models/ live in ~/Library/Application Support/WhisperLocal, so the signed bundle is never written to.
/// </summary>
internal static class AppPaths
{
    public const string ConfigFileName = "config.json";

    /// <summary>Directory of the executable and the bundled runtime.</summary>
    public static string AppDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Directory of the config, logs and models.</summary>
    public static string DataDir { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "WhisperLocal")
        : AppDir;

    public static string ConfigPath => Path.Combine(DataDir, ConfigFileName);

    /// <summary>The config shipped with the build; on macOS it is copied to <see cref="ConfigPath"/>.</summary>
    public static string DefaultConfigPath => Path.Combine(AppDir, ConfigFileName);

    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Relative config paths are relative to the data directory; absolute paths are used as is.</summary>
    public static string Resolve(string path) => Path.GetFullPath(path, DataDir);

    /// <summary>Relative paths of bundled executables are relative to the executable's directory.</summary>
    public static string ResolveRuntime(string path) => Path.GetFullPath(path, AppDir);
}
