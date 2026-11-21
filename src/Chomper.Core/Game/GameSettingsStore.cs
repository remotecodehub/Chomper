using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework.Input;

namespace Chomper.Core.Game;

/// <summary>Persistent desktop/mobile preferences for Chomper.</summary>
internal sealed class GameSettings
{
    public bool FullscreenEnabled { get; set; }
    public bool HintsEnabled { get; set; } = true;
    public int GhostOneButton { get; set; } = (int)Buttons.A;
    public int GhostTwoButton { get; set; } = (int)Buttons.B;
    public int GhostThreeButton { get; set; } = (int)Buttons.Y;
    public int TrapButton { get; set; } = (int)Buttons.X;
    public int PauseButton { get; set; } = (int)Buttons.Start;
    public int TrapKey { get; set; } = (int)Keys.F;
}

/// <summary>Stores settings and the last explicitly saved gameplay snapshot.</summary>
internal static class GameSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Loads preferences, returning safe defaults if no file exists.</summary>
    public static GameSettings Load()
    {
        try
        {
            var path = SettingsPath();
            return File.Exists(path)
                ? JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path)) ?? new GameSettings()
                : new GameSettings();
        }
        catch
        {
            return new GameSettings();
        }
    }

    /// <summary>Persists preferences without making settings failures fatal to gameplay.</summary>
    public static void Save(GameSettings settings)
    {
        try
        {
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Read-only mobile sandboxes may reject persistence; gameplay remains usable.
        }
    }

    /// <summary>Saves the explicit pause-menu progress snapshot for a future continue flow.</summary>
    public static void SaveProgress(int phase, int score, int playerCount, int difficulty, IEnumerable<string> remainingPellets)
    {
        try
        {
            var path = ProgressPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var snapshot = new SavedProgress
            {
                Phase = phase,
                Score = score,
                PlayerCount = playerCount,
                Difficulty = difficulty,
                RemainingPellets = new List<string>(remainingPellets)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
            // Saving is best effort and must not interrupt returning to the menu.
        }
    }

    /// <summary>Builds the platform-appropriate application-data path.</summary>
    private static string SettingsPath() => Path.Combine(DataDirectory(), "settings.json");

    /// <summary>Builds the path for the explicit gameplay progress snapshot.</summary>
    private static string ProgressPath() => Path.Combine(DataDirectory(), "progress.json");

    /// <summary>Returns a writable application-data directory when the platform exposes one.</summary>
    private static string DataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "Chomper");
    }

    /// <summary>Serializable representation of a saved phase.</summary>
    private sealed class SavedProgress
    {
        public int Phase { get; set; }
        public int Score { get; set; }
        public int PlayerCount { get; set; }
        public int Difficulty { get; set; }
        public List<string> RemainingPellets { get; set; } = new();
    }
}
