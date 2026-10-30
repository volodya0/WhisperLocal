using WhisperLocal.Config;
using WhisperLocal.Pipeline;

namespace WhisperLocal.Ui;

public enum SetupState { Ok, Warning, Error, Pending }

/// <summary>One line of the setup checklist, with an optional button that fixes it.</summary>
public sealed record SetupItem(SetupState State, string Text, string? ActionLabel = null, Action? Action = null);

/// <summary>A hotkey offered in the selector; <paramref name="Key"/> is the config value (Hotkey.Key).</summary>
public sealed record HotkeyPreset(string Title, string Key);

/// <summary>What the shared windows need from the platform app. Called on the UI thread.</summary>
public interface IAppHost
{
    ConfigManager Config { get; }
    DictationHistory History { get; }
    DictationStats Stats { get; }
    string VersionText { get; }

    /// <summary>Permissions, server, model, hotkey: what has to be in place before dictation works.</summary>
    IReadOnlyList<SetupItem> GetSetupItems();

    /// <summary>Whether the app has the grammar check; without it the main window leaves out the Language model card.</summary>
    bool HasGrammarCheck { get; }

    /// <summary>Listening tells speakers apart with a voice model (macOS): its row on the Models page.</summary>
    bool HasSpeakerSeparation => false;

    /// <summary>Grammar check: the language model (Download, Unload) and its server.</summary>
    IReadOnlyList<SetupItem> GetLanguageModelItems();

    IReadOnlyList<HotkeyPreset> HotkeyPresets { get; }
    string DescribeHotkey(string key);

    /// <summary>
    /// Captures the next key combination the user holds and releases. <paramref name="preview"/> gets the keys
    /// held so far; <paramref name="done"/> the config value, or null if cancelled. Returns an error message
    /// instead when recording is not possible right now.
    /// </summary>
    string? StartHotkeyRecording(Action<string> preview, Action<string?> done);

    void CancelHotkeyRecording();

    /// <summary>Whether the speech model is loaded (whisper-server running) and a short description.</summary>
    (bool Loaded, string Detail) GetModelMemory();

    /// <summary>
    /// The models in memory now: a name and a detail (what uses it, how much memory). By default the speech model when
    /// <see cref="GetModelMemory"/> says it is loaded.
    /// </summary>
    IReadOnlyList<(string Name, string Detail)> GetLoadedModels() =>
        GetModelMemory() is (true, var detail) ? [("Speech model", detail)] : [];

    /// <summary>Unloads every model (frees memory); the next use loads them again.</summary>
    Task UnloadAsync();
    Task LoadAsync();

    /// <summary>
    /// Downloads a model into the models folder: a speech model (a Whisper.ModelPath value), or with
    /// <paramref name="url"/> a language model. One download of each kind at a time; others are ignored.
    /// </summary>
    void DownloadModel(string modelPath, string? url = null);

    /// <summary>"Downloading …: 40%" while <paramref name="modelPath"/> is being downloaded, otherwise null.</summary>
    string? DownloadProgress(string modelPath);

    void CancelDownload(string modelPath);

    /// <summary>Looks for the newest Full or Lite release and offers to install it (switching edition if it is the other one).</summary>
    void CheckForUpdates(bool lite);

    /// <summary>Opens a file, folder or web address; <paramref name="textEditor"/> forces a text editor.</summary>
    void OpenPath(string path, bool textEditor = false);

    /// <summary>The main window was shown or hidden (macOS brings the app to the front when it is shown).</summary>
    void MainWindowVisibilityChanged(bool visible);
}
