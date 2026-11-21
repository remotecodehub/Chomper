using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Media;

namespace Chomper.Core.Audio;

/// <summary>
/// Identifies the music contexts used by menus, gameplay and result screens.
/// </summary>
public enum AudioMusicTrack
{
    /// <summary>Music used by the main menu and configuration screens.</summary>
    Menu,

    /// <summary>Music used during an active gameplay phase.</summary>
    Gameplay,

    /// <summary>Music used while the pause confirmation dialog is open.</summary>
    Pause,

    /// <summary>Music used after Chomper reaches a game-over condition.</summary>
    GameOver,

    /// <summary>Music used after the player completes the capture objective.</summary>
    Victory
}

/// <summary>
/// Identifies short sound effects used by the Chomper interface and gameplay.
/// </summary>
public enum AudioEffect
{
    /// <summary>Short sound used when a menu item is highlighted.</summary>
    MenuMove,

    /// <summary>Sound used when a menu action is accepted.</summary>
    MenuConfirm,

    /// <summary>Sound used when returning to a previous menu.</summary>
    MenuBack,

    /// <summary>Sound used when a gameplay phase starts.</summary>
    GameStart,

    /// <summary>Sound used when a pellet is collected.</summary>
    PelletCollected,

    /// <summary>Sound used when a power fruit is collected.</summary>
    PowerFruitCollected,

    /// <summary>Sound used when a trap fruit is placed or consumed.</summary>
    Trap,

    /// <summary>Sound used when a Chomper captures a ghost.</summary>
    GhostDefeated,

    /// <summary>Sound used when a Chomper is captured.</summary>
    ChomperCaptured,

    /// <summary>Sound used when the game-over result is shown.</summary>
    GameOver,

    /// <summary>Sound used when the victory result is shown.</summary>
    Victory
}

/// <summary>
/// Centralizes every BGM and SFX operation for all Chomper screens.
/// </summary>
/// <remarks>
/// The game owns the ContentManager and passes it into this service. MonoGame
/// owns the loaded Song and SoundEffect instances through that ContentManager;
/// this service stops playback and releases references when disposed, without
/// disposing content objects behind the ContentManager's back.
/// </remarks>
public sealed class AudioService : IDisposable
{
    private readonly ContentManager content;
    private readonly Dictionary<AudioMusicTrack, Song> music = new();
    private readonly Dictionary<AudioEffect, SoundEffect> effects = new();
    private AudioMusicTrack? currentTrack;
    private bool disposed;
    private float musicVolume = .65f;
    private float effectsVolume = .85f;
    private bool musicMuted;
    private bool effectsMuted;

    /// <summary>Creates the audio service using the game's content manager.</summary>
    /// <param name="contentManager">Content manager that loads the generated XNB assets.</param>
    public AudioService(ContentManager contentManager)
    {
        content = contentManager ?? throw new ArgumentNullException(nameof(contentManager));
    }

    /// <summary>Gets or sets the normalized music volume from zero to one.</summary>
    public float MusicVolume
    {
        get => musicVolume;
        set
        {
            musicVolume = MathHelper.Clamp(value, 0f, 1f);
            ApplyMusicVolume();
        }
    }

    /// <summary>Gets or sets the normalized sound-effect volume from zero to one.</summary>
    public float EffectsVolume
    {
        get => effectsVolume;
        set => effectsVolume = MathHelper.Clamp(value, 0f, 1f);
    }

    /// <summary>Gets or sets whether BGM playback is muted.</summary>
    public bool MusicMuted
    {
        get => musicMuted;
        set
        {
            musicMuted = value;
            ApplyMusicVolume();
        }
    }

    /// <summary>Gets or sets whether SFX playback is muted.</summary>
    public bool EffectsMuted
    {
        get => effectsMuted;
        set => effectsMuted = value;
    }

    /// <summary>Loads every BGM and SFX asset once from the MonoGame content pipeline.</summary>
    public void LoadContent()
    {
        ThrowIfDisposed();

        LoadMusic(AudioMusicTrack.Menu, "Audio/bgm-retro-arcade-game-music");
        LoadMusic(AudioMusicTrack.Gameplay, "Audio/bgm-arcade-style-game");
        LoadMusic(AudioMusicTrack.Pause, "Audio/bgm-retro-game-arcade");
        LoadMusic(AudioMusicTrack.GameOver, "Audio/bgm-man-is-he-mega");
        LoadMusic(AudioMusicTrack.Victory, "Audio/bgm-pixel-art");

        LoadEffect(AudioEffect.GameStart, "Audio/sfx-game-start");
        LoadEffect(AudioEffect.MenuConfirm, "Audio/sfx-confirm-tap");
        LoadEffect(AudioEffect.MenuMove, "Audio/sfx-coin-collect");
        LoadEffect(AudioEffect.PelletCollected, "Audio/sfx-coin-collect");
        LoadEffect(AudioEffect.PowerFruitCollected, "Audio/sfx-powerup-1");
        LoadEffect(AudioEffect.Trap, "Audio/sfx-powerup-2");
        LoadEffect(AudioEffect.GhostDefeated, "Audio/sfx-item-pickup");
        LoadEffect(AudioEffect.ChomperCaptured, "Audio/sfx-countdown");
        LoadEffect(AudioEffect.GameOver, "Audio/sfx-game-over");
        LoadEffect(AudioEffect.Victory, "Audio/sfx-item-pickup");
    }

    /// <summary>Starts or resumes the requested looping music track.</summary>
    /// <param name="track">Music context to play.</param>
    /// <param name="restart">Restarts playback when the same track is already active.</param>
    public void PlayMusic(AudioMusicTrack track, bool restart = false)
    {
        ThrowIfDisposed();

        if (!restart && currentTrack == track && MediaPlayer.State == MediaState.Playing)
            return;

        if (!music.TryGetValue(track, out var song))
            return;

        currentTrack = track;
        MediaPlayer.IsRepeating = true;
        MediaPlayer.Volume = musicMuted ? 0f : musicVolume;
        MediaPlayer.Play(song);
    }

    /// <summary>Stops all currently playing music.</summary>
    public void StopMusic()
    {
        ThrowIfDisposed();
        MediaPlayer.Stop();
        currentTrack = null;
    }

    /// <summary>Plays a short non-looping sound effect at the configured volume.</summary>
    /// <param name="effect">Effect identifier to play.</param>
    public void PlayEffect(AudioEffect effect)
    {
        ThrowIfDisposed();

        if (effectsMuted || !effects.TryGetValue(effect, out var soundEffect))
            return;

        soundEffect.Play(effectsVolume, 0f, 0f);
    }

    /// <summary>Stops playback and releases service references before game shutdown.</summary>
    public void Dispose()
    {
        if (disposed)
            return;

        MediaPlayer.Stop();
        MediaPlayer.IsRepeating = false;
        currentTrack = null;
        music.Clear();
        effects.Clear();
        disposed = true;
    }

    /// <summary>Loads a BGM asset and keeps failures isolated to that track.</summary>
    private void LoadMusic(AudioMusicTrack track, string assetName)
    {
        try
        {
            music[track] = content.Load<Song>(assetName);
        }
        catch (ContentLoadException)
        {
            // A missing optional track must not prevent menus or gameplay from starting.
        }
    }

    /// <summary>Loads an SFX asset and keeps failures isolated to that effect.</summary>
    private void LoadEffect(AudioEffect effect, string assetName)
    {
        try
        {
            effects[effect] = content.Load<SoundEffect>(assetName);
        }
        catch (ContentLoadException)
        {
            // A missing optional effect must not prevent the game from starting.
        }
    }

    /// <summary>Applies the current mute state and volume to the active BGM.</summary>
    private void ApplyMusicVolume()
    {
        MediaPlayer.Volume = musicMuted ? 0f : musicVolume;
    }

    /// <summary>Throws when a caller attempts to use a disposed service.</summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
