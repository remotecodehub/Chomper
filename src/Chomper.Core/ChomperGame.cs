using System;
using System.Collections.Generic;
using System.Linq;
using Chomper.Core.Game;
using Chomper.Core.Localization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace Chomper.Core;

/// <summary>
/// Reversed Chomper gameplay: the player controls one of three ghosts and the
/// other living ghosts become NPC allies. The maze and all gameplay sprites are
/// generated at runtime; no Tiled map or gameplay image is required.
/// </summary>
public sealed class ChomperGame : Microsoft.Xna.Framework.Game
{
    private const int LogicalWidth = 800;
    private const int LogicalHeight = 560;
    private const int TileSize = 24;
    private const float ReadyDelay = 0.65f;
    private const float PowerFruitDuration = 8f;
    private const float NormalGhostSpeed = 64f;
    private const float TrapDuration = 4f;
    private const int ChomperLives = 3;
    private const int TrapFruitsPerGhost = 3;
    private const int AlgorithmSwitchEveryPellets = 8;

    private readonly GraphicsDeviceManager graphics;
    private readonly Random random = new(51937);
    private readonly OnlineLearningPolicy ghostPolicy = new();
    private readonly OnlineLearningPolicy chomperPolicy = new();
    private readonly List<Ghost> ghosts = new();
    private readonly List<Chomper> chompers = new();
    private readonly List<TrapFruit> traps = new();

    private SpriteBatch spriteBatch;
    private Texture2D pixel;
    private SpriteFont hudFont;
    private ProceduralMaze maze;
    private KeyboardState previousKeyboard;
    private GamePadState previousGamePad;
    private GamePadState previousGamePadTwo;
    private GamePadState previousGamePadThree;
    private int phase;
    private int selectedGhost;
    private int score;
    private int pelletsCollected;
    private int pelletCount;
    private int chomperLives;
    private int chompersCaptured;
    private bool allGhostsDefeated;
    private int releaseThreshold;
    private float helpersUnlockedAt = -1f;
    private float elapsed;
    private float powerFruitTime;
    private NavigationAlgorithm chomperAlgorithm = NavigationAlgorithm.AStar;
    private GameState state;
    private Point previousPlayerCell;
    private MazeDirection swipeDirection;
    private int menuSelection;
    private int difficultySelection = 1;
    private int optionsSelection;
    private int controlsSelection;
    private int pauseSelection;
    private bool hintsEnabled = true;
    private bool fullscreenEnabled;
    private bool waitingForControl;
    private bool waitingForGamepad;
    private int playerCount = 1;
    private GameSettings settings = new();
    private Buttons ghostOneButton = Buttons.A;
    private Buttons ghostTwoButton = Buttons.B;
    private Buttons ghostThreeButton = Buttons.Y;
    private Buttons trapButton = Buttons.X;
    private Buttons pauseButton = Buttons.Start;
    private Keys trapKey = Keys.F;
    private DifficultyLevel difficulty = DifficultyLevel.Normal;

    /// <summary>Computes the centered logical X origin for the maze.</summary>
    private int OriginX => (graphics.PreferredBackBufferWidth - ProceduralMaze.Width * TileSize) / 2;
    private const int OriginY = 72;
    /// <summary>Returns the currently selected ghost.</summary>
    private Ghost Selected => ghosts[selectedGhost];

    private enum GameState
    {
        Menu,
        Difficulty,
        Options,
        Controls,
        PauseConfirmation,
        Playing,
        GameOver,
        Won
    }

    private enum DifficultyLevel
    {
        Easy,
        Normal,
        Hard
    }

    /// <summary>Identifies Android and iOS targets where physical input is disabled.</summary>
    private bool IsMobile => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
    /// <summary>Returns the ghost speed selected by the difficulty screen.</summary>
    private float GhostSpeed => difficulty switch
    {
        DifficultyLevel.Easy => 56f,
        DifficultyLevel.Hard => 76f,
        _ => NormalGhostSpeed
    };
    /// <summary>Returns Chomper's normal chase speed relative to the ghosts.</summary>
    private float ChomperSpeed => GhostSpeed * 1.75f;
    /// <summary>Returns Chomper's speed while affected by a trap fruit.</summary>
    private float TrappedChomperSpeed => GhostSpeed * .5f;

    private abstract class Actor
    {
        public Point Cell;
        public Point TargetCell;
        public Vector2 Position;
        public MazeDirection Direction = MazeDirection.Up;
        public MazeDirection DesiredDirection = MazeDirection.Up;
        public bool IsMoving;
        public bool Alive = true;

        /// <summary>Creates an actor at a maze cell before movement starts.</summary>
        protected Actor(Point cell) => Cell = cell;
    }

    private sealed class Ghost : Actor
    {
        public string Name { get; }
        public Color Color { get; }
        public NavigationAlgorithm Algorithm { get; }
        public float ReleaseDelay { get; }
        public bool Released;
        public PlayerIndex? Player;
        public int TrapFruitsRemaining;

        /// <summary>Creates a colored ghost with its NPC strategy and release delay.</summary>
        public Ghost(string name, Color color, NavigationAlgorithm algorithm, Point cell, float releaseDelay)
            : base(cell)
        {
            Name = name;
            Color = color;
            Algorithm = algorithm;
            ReleaseDelay = releaseDelay;
            TrapFruitsRemaining = TrapFruitsPerGhost;
        }
    }

    private sealed class Chomper : Actor
    {
        public Color Color { get; }
        public int PelletsSinceAlgorithmChange;
        public float PowerTime;
        public float TrapTime;
        public Point ObjectiveCell;
        public bool HasObjective;
        public Queue<Point> RecentCells { get; } = new();

        /// <summary>Indicates whether a power fruit or trap currently permits capture.</summary>
        public bool IsVulnerable => PowerTime > 0f || TrapTime > 0f;

        /// <summary>Creates a Chomper actor at a maze cell.</summary>
        public Chomper(Color color, Point cell) : base(cell) => Color = color;
    }

    private sealed class TrapFruit
    {
        public Point Cell { get; }

        /// <summary>Creates a trap fruit at a maze cell.</summary>
        public TrapFruit(Point cell) => Cell = cell;
    }

    /// <summary>Creates the game and configures the shared logical backbuffer.</summary>
    public ChomperGame()
    {
        graphics = new GraphicsDeviceManager(this);
        graphics.PreferredBackBufferWidth = LogicalWidth;
        graphics.PreferredBackBufferHeight = LogicalHeight;
        graphics.SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight;
        graphics.IsFullScreen = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
        Content.RootDirectory = "Content";
        IsMouseVisible = !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
    }

    /// <summary>Initializes localization and touch gestures for the current platform.</summary>
    protected override void Initialize()
    {
        settings = GameSettingsStore.Load();
        hintsEnabled = settings.HintsEnabled;
        fullscreenEnabled = settings.FullscreenEnabled;
        ghostOneButton = (Buttons)settings.GhostOneButton;
        ghostTwoButton = (Buttons)settings.GhostTwoButton;
        ghostThreeButton = (Buttons)settings.GhostThreeButton;
        trapButton = (Buttons)settings.TrapButton;
        pauseButton = (Buttons)settings.PauseButton;
        trapKey = (Keys)settings.TrapKey;
        graphics.IsFullScreen = IsMobile || fullscreenEnabled;
        LocalizationManager.SetCulture(LocalizationManager.DEFAULT_CULTURE_CODE);
        TouchPanel.EnabledGestures = GestureType.Flick | GestureType.Tap | GestureType.DoubleTap | GestureType.Hold;
        base.Initialize();
        // The graphics device is ready only after base.Initialize; apply the
        // persisted desktop mode here so a new process starts fullscreen.
        if (!IsMobile) graphics.ApplyChanges();
    }

    /// <summary>Loads the Array font and creates runtime-only pixel primitives.</summary>
    protected override void LoadContent()
    {
        spriteBatch = new SpriteBatch(GraphicsDevice);
        hudFont = Content.Load<SpriteFont>("Fonts/Hud");
        pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData(new[] { RetroPalette.White });
        StartPhase(false);
        state = GameState.Menu;
    }

    /// <summary>Creates a seeded phase, resets actors, and applies the selected mode.</summary>
    private void StartPhase(bool nextPhase)
    {
        if (nextPhase) phase = (phase + 1) % 7;
        maze = ProceduralMaze.Generate(phase);
        ghosts.Clear();
        chompers.Clear();
        traps.Clear();
        ghosts.Add(new Ghost("VERDE", RetroPalette.Green, NavigationAlgorithm.AStar,
            maze.GhostStarts[0], 0f));
        ghosts.Add(new Ghost("AMARELO", RetroPalette.Yellow, NavigationAlgorithm.TravellerSalesman,
            maze.GhostStarts[1], 2.5f));
        ghosts.Add(new Ghost("AZUL", RetroPalette.Blue, NavigationAlgorithm.FiniteStateMachine,
            maze.GhostStarts[2], 4.5f));
        ghosts[0].Player = playerCount >= 1 ? PlayerIndex.One : null;
        ghosts[1].Player = playerCount >= 2 ? PlayerIndex.Two : null;
        ghosts[2].Player = playerCount >= 3 ? PlayerIndex.Three : null;
        foreach (var ghost in ghosts) ghost.Position = CellToWorld(ghost.Cell);

        chompers.Add(new Chomper(RetroPalette.Red, maze.ChomperStarts[0]));
        chompers[0].Position = CellToWorld(maze.ChomperStarts[0]);
        pelletCount = maze.WalkableCells().Count(maze.HasPellet);
        releaseThreshold = Math.Max(1, (int)Math.Ceiling(pelletCount * .25f));
        helpersUnlockedAt = -1f;
        chomperLives = ChomperLives;
        chompersCaptured = 0;
        allGhostsDefeated = false;
        selectedGhost = 0;
        score = 0;
        pelletsCollected = 0;
        elapsed = 0f;
        powerFruitTime = 0f;
        chomperAlgorithm = NavigationAlgorithm.AStar;
        swipeDirection = MazeDirection.None;
        previousGamePad = default;
        previousGamePadTwo = default;
        previousGamePadThree = default;
        state = GameState.Playing;
        previousPlayerCell = Selected.Cell;
    }

    /// <summary>Advances menus, input, actors, collectibles, and collision rules.</summary>
    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        var gamePad = IsMobile ? default : GamePad.GetState(PlayerIndex.One);
        var gamePadTwo = IsMobile ? default : GamePad.GetState(PlayerIndex.Two);
        var gamePadThree = IsMobile ? default : GamePad.GetState(PlayerIndex.Three);
        var pausePressed = !IsMobile &&
            ((keyboard.IsKeyDown(Keys.Escape) && previousKeyboard.IsKeyUp(Keys.Escape)) ||
             (gamePad.IsButtonDown(pauseButton) && previousGamePad.IsButtonUp(pauseButton)) ||
             (gamePad.IsButtonDown(Buttons.Back) && previousGamePad.IsButtonUp(Buttons.Back)));
        if (state == GameState.Playing && pausePressed)
        {
            state = GameState.PauseConfirmation;
            pauseSelection = 0;
            previousKeyboard = keyboard;
            previousGamePad = gamePad;
            previousGamePadTwo = gamePadTwo;
            previousGamePadThree = gamePadThree;
            base.Update(gameTime);
            return;
        }

        if (state is GameState.Menu or GameState.Difficulty or GameState.Options or GameState.Controls or GameState.PauseConfirmation)
        {
            UpdateFrontEnd(keyboard, gamePad);
            previousKeyboard = keyboard;
            previousGamePad = gamePad;
            previousGamePadTwo = gamePadTwo;
            previousGamePadThree = gamePadThree;
            base.Update(gameTime);
            return;
        }

        if (state != GameState.Playing)
        {
            if (!IsMobile && keyboard.IsKeyDown(Keys.Enter) && previousKeyboard.IsKeyUp(Keys.Enter))
                StartPhase(state == GameState.Won);
            if (IsMobile) HandleEndScreenTouch(state == GameState.Won);
            previousKeyboard = keyboard;
            base.Update(gameTime);
            return;
        }

        var delta = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.05f);
        elapsed += delta;
        powerFruitTime = Math.Max(0f, powerFruitTime - delta);
        HandleSwitchInput(keyboard, gamePad);
        HandleAdditionalPlayerInput(gamePadTwo, gamePadThree);
        ReleaseGhosts();

        Selected.DesiredDirection = ReadPlayerDirection();
        LearnFromPlayer();
        UpdateGhosts(delta);
        UpdateChompers(delta);
        CollectChomperItems();
        ResolveCollisions();

        pelletsCollected = pelletCount - maze.RemainingPelletCount();
        EvaluatePelletGameOver();

        previousKeyboard = keyboard;
        previousGamePad = gamePad;
        previousGamePadTwo = gamePadTwo;
        previousGamePadThree = gamePadThree;
        Window.Title = GameText.Format("WindowTitlePlaying", phase + 1, score, chomperLives);
        base.Update(gameTime);
    }

    /// <summary>Allows a mobile tap to restart or advance after an ended phase.</summary>
    private void HandleEndScreenTouch(bool advancePhase)
    {
        while (TouchPanel.IsGestureAvailable)
        {
            var gesture = TouchPanel.ReadGesture();
            if (gesture.GestureType is GestureType.Tap or GestureType.DoubleTap or GestureType.Hold)
            {
                StartPhase(advancePhase);
                return;
            }
        }
    }

    /// <summary>Processes menu input from desktop devices or touch gestures.</summary>
    private void UpdateFrontEnd(KeyboardState keyboard, GamePadState gamePad)
    {
        if (state == GameState.Controls && waitingForControl && !IsMobile)
        {
            foreach (var key in Enum.GetValues<Keys>())
            {
                if (key is Keys.Enter or Keys.Escape || !keyboard.IsKeyDown(key) || !previousKeyboard.IsKeyUp(key)) continue;
                trapKey = key;
                waitingForControl = false;
                PersistSettings();
                break;
            }
        }
        if (state == GameState.Controls && waitingForGamepad && !IsMobile)
        {
            var assignedButton = FirstNewGamepadButton(gamePad);
            if (assignedButton != Buttons.None)
            {
                AssignGamepadButton(assignedButton);
                waitingForGamepad = false;
                PersistSettings();
                gamePad = default;
            }
        }
        var movedUp = !IsMobile &&
            ((keyboard.IsKeyDown(Keys.Up) && previousKeyboard.IsKeyUp(Keys.Up)) ||
             (gamePad.IsButtonDown(Buttons.DPadUp) && previousGamePad.IsButtonUp(Buttons.DPadUp)));
        var movedDown = !IsMobile &&
            ((keyboard.IsKeyDown(Keys.Down) && previousKeyboard.IsKeyUp(Keys.Down)) ||
             (gamePad.IsButtonDown(Buttons.DPadDown) && previousGamePad.IsButtonUp(Buttons.DPadDown)));
        var accepted = !IsMobile &&
            ((keyboard.IsKeyDown(Keys.Enter) && previousKeyboard.IsKeyUp(Keys.Enter)) ||
             (gamePad.IsButtonDown(Buttons.A) && previousGamePad.IsButtonUp(Buttons.A)));
        var back = !IsMobile &&
            ((keyboard.IsKeyDown(Keys.Back) && previousKeyboard.IsKeyUp(Keys.Back)) ||
             (keyboard.IsKeyDown(Keys.Escape) && previousKeyboard.IsKeyUp(Keys.Escape)) ||
             (gamePad.IsButtonDown(Buttons.B) && previousGamePad.IsButtonUp(Buttons.B)) ||
             (gamePad.IsButtonDown(Buttons.Back) && previousGamePad.IsButtonUp(Buttons.Back)));

        while (TouchPanel.IsGestureAvailable)
        {
            var gesture = TouchPanel.ReadGesture();
            if (!IsMobile) continue;
            if (gesture.GestureType == GestureType.Flick)
            {
                movedUp |= gesture.Delta.Y < -12f;
                movedDown |= gesture.Delta.Y > 12f;
            }
            else if (gesture.GestureType is GestureType.Tap or GestureType.DoubleTap or GestureType.Hold)
            {
                var logical = ToLogical(gesture.Position);
                if (gesture.GestureType != GestureType.Tap || logical.Y > 80f)
                    accepted = true;
            }
        }

        if (movedUp) MoveFrontEndSelection(-1);
        if (movedDown) MoveFrontEndSelection(1);
        if (back) GoBackFromFrontEnd();
        if (accepted) ActivateFrontEndSelection();
    }

    /// <summary>Moves the active menu cursor with wraparound.</summary>
    private void MoveFrontEndSelection(int offset)
    {
        var count = state switch
        {
            GameState.Menu => 5,
            GameState.Difficulty => 4,
            GameState.Options => 4,
            GameState.Controls => 7,
            GameState.PauseConfirmation => 3,
            _ => 1
        };
        if (state == GameState.Menu)
            menuSelection = (menuSelection + offset + count) % count;
        else if (state == GameState.Difficulty)
            difficultySelection = (difficultySelection + offset + count) % count;
        else if (state == GameState.Options)
            optionsSelection = (optionsSelection + offset + count) % count;
        else if (state == GameState.Controls)
            controlsSelection = (controlsSelection + offset + count) % count;
        else
            pauseSelection = (pauseSelection + offset + count) % count;
    }

    /// <summary>Activates the current menu item or toggles its option.</summary>
    private void ActivateFrontEndSelection()
    {
        switch (state)
        {
            case GameState.Menu:
                switch (menuSelection)
                {
                    case 0: playerCount = 1; state = GameState.Difficulty; difficultySelection = (int)difficulty; break;
                    case 1: playerCount = 2; state = GameState.Difficulty; difficultySelection = (int)difficulty; break;
                    case 2: playerCount = 3; state = GameState.Difficulty; difficultySelection = (int)difficulty; break;
                    case 3: state = GameState.Options; optionsSelection = 0; break;
                    case 4: Exit(); break;
                }
                break;
            case GameState.Difficulty:
                if (difficultySelection == 3)
                    state = GameState.Menu;
                else
                {
                    difficulty = (DifficultyLevel)difficultySelection;
                    StartPhase(false);
                }
                break;
            case GameState.Options:
                if (optionsSelection == 0)
                {
                    hintsEnabled = !hintsEnabled;
                    PersistSettings();
                }
                else if (optionsSelection == 1)
                {
                    fullscreenEnabled = !fullscreenEnabled;
                    graphics.IsFullScreen = IsMobile || fullscreenEnabled;
                    if (!IsMobile) graphics.ApplyChanges();
                    PersistSettings();
                }
                else if (optionsSelection == 2)
                {
                    state = GameState.Controls;
                    controlsSelection = 0;
                }
                else state = GameState.Menu;
                break;
            case GameState.Controls:
                if (controlsSelection == 6)
                    state = GameState.Options;
                else if (controlsSelection == 0)
                    waitingForControl = !waitingForControl;
                else
                    waitingForGamepad = !waitingForGamepad;
                break;
            case GameState.PauseConfirmation:
                if (pauseSelection == 0)
                    SaveProgressAndReturnToMenu();
                else if (pauseSelection == 1)
                    ReturnToMenuWithoutSaving();
                else
                    state = GameState.Playing;
                break;
        }
    }

    /// <summary>Returns from a child front-end screen to the main menu.</summary>
    private void GoBackFromFrontEnd()
    {
        state = state switch
        {
            GameState.PauseConfirmation => GameState.Playing,
            GameState.Controls => GameState.Options,
            _ => GameState.Menu
        };
        waitingForControl = false;
        waitingForGamepad = false;
    }

    /// <summary>Returns the first newly pressed configurable gamepad button.</summary>
    private Buttons FirstNewGamepadButton(GamePadState gamePad)
    {
        var candidates = new[]
        {
            Buttons.A, Buttons.B, Buttons.X, Buttons.Y,
            Buttons.LeftShoulder, Buttons.RightShoulder, Buttons.Start, Buttons.Back
        };
        foreach (var button in candidates)
            if (gamePad.IsButtonDown(button) && previousGamePad.IsButtonUp(button)) return button;
        return Buttons.None;
    }

    /// <summary>Assigns the captured gamepad button to the selected control action.</summary>
    private void AssignGamepadButton(Buttons button)
    {
        switch (controlsSelection)
        {
            case 1: ghostOneButton = button; break;
            case 2: ghostTwoButton = button; break;
            case 3: ghostThreeButton = button; break;
            case 4: trapButton = button; break;
            case 5: pauseButton = button; break;
        }
    }

    /// <summary>Copies current preferences to the persistent settings store.</summary>
    private void PersistSettings()
    {
        settings.FullscreenEnabled = fullscreenEnabled;
        settings.HintsEnabled = hintsEnabled;
        settings.GhostOneButton = (int)ghostOneButton;
        settings.GhostTwoButton = (int)ghostTwoButton;
        settings.GhostThreeButton = (int)ghostThreeButton;
        settings.TrapButton = (int)trapButton;
        settings.PauseButton = (int)pauseButton;
        settings.TrapKey = (int)trapKey;
        GameSettingsStore.Save(settings);
    }

    /// <summary>Saves the current phase snapshot and returns to the main menu.</summary>
    private void SaveProgressAndReturnToMenu()
    {
        GameSettingsStore.SaveProgress(phase, score, playerCount, (int)difficulty,
            maze.RemainingPellets().Select(cell => $"{cell.X},{cell.Y}"));
        ReturnToMenuWithoutSaving();
    }

    /// <summary>Returns to the main menu without writing a gameplay snapshot.</summary>
    private void ReturnToMenuWithoutSaving()
    {
        state = GameState.Menu;
        waitingForControl = false;
        waitingForGamepad = false;
    }

    /// <summary>Handles ghost switching, traps, and mobile gestures during gameplay.</summary>
    private void HandleSwitchInput(KeyboardState keyboard, GamePadState gamePad)
    {
        if (!IsMobile)
        {
            if (playerCount == 1)
            {
                if (keyboard.IsKeyDown(Keys.D1) && previousKeyboard.IsKeyUp(Keys.D1)) SwitchTo(0);
                if (keyboard.IsKeyDown(Keys.D2) && previousKeyboard.IsKeyUp(Keys.D2)) SwitchTo(1);
                if (keyboard.IsKeyDown(Keys.D3) && previousKeyboard.IsKeyUp(Keys.D3)) SwitchTo(2);
                if (keyboard.IsKeyDown(Keys.Tab) && previousKeyboard.IsKeyUp(Keys.Tab)) SwitchToNext();
                if (gamePad.IsButtonDown(ghostOneButton) && previousGamePad.IsButtonUp(ghostOneButton)) SwitchTo(0);
                if (gamePad.IsButtonDown(ghostTwoButton) && previousGamePad.IsButtonUp(ghostTwoButton)) SwitchTo(1);
                if (gamePad.IsButtonDown(ghostThreeButton) && previousGamePad.IsButtonUp(ghostThreeButton)) SwitchTo(2);
            }
            if ((keyboard.IsKeyDown(Keys.Space) && previousKeyboard.IsKeyUp(Keys.Space))
                || (keyboard.IsKeyDown(trapKey) && previousKeyboard.IsKeyUp(trapKey))
                || (gamePad.IsButtonDown(trapButton) && previousGamePad.IsButtonUp(trapButton)))
                TryDropTrap(Selected);
        }

        while (TouchPanel.IsGestureAvailable)
        {
            var gesture = TouchPanel.ReadGesture();
            if (gesture.GestureType == GestureType.Flick && gesture.Delta.LengthSquared() >= 25f)
            {
                swipeDirection = FromVector(gesture.Delta);
                continue;
            }
            if (gesture.GestureType is GestureType.Hold or GestureType.DoubleTap)
            {
                if (IsMobile) TryDropTrap(Selected);
                continue;
            }
            if (gesture.GestureType != GestureType.Tap) continue;
            var logical = ToLogical(gesture.Position);
            var x = logical.X;
            var y = logical.Y;
            if (playerCount == 1 && y <= OriginY && x >= 12 && x <= 168)
                SwitchTo(MathHelper.Clamp((int)((x - 12) / 52), 0, 2));
        }
    }

    /// <summary>Selects the next living ghost in roster order.</summary>
    private void SwitchToNext()
    {
        for (var offset = 1; offset <= ghosts.Count; offset++)
        {
            var next = (selectedGhost + offset) % ghosts.Count;
            if (ghosts[next].Alive)
            {
                SwitchTo(next);
                return;
            }
        }
    }

    /// <summary>Transfers primary control to a living ghost by roster index.</summary>
    private void SwitchTo(int index)
    {
        if (index < 0 || index >= ghosts.Count || !ghosts[index].Alive) return;
        selectedGhost = index;
        ghosts[index].Released = true;
        ghosts[index].DesiredDirection = MazeDirection.Up;
        previousPlayerCell = ghosts[index].Cell;
    }

    /// <summary>Releases the primary ghost and delayed NPC helpers at their thresholds.</summary>
    private void ReleaseGhosts()
    {
        if (helpersUnlockedAt < 0f && pelletsCollected >= releaseThreshold)
            helpersUnlockedAt = elapsed;

        for (var i = 0; i < ghosts.Count; i++)
        {
            var ghost = ghosts[i];
            var canLeave = i == 0
                ? elapsed >= ReadyDelay
                : ghost.Player is not null
                    ? elapsed >= ReadyDelay + ghost.ReleaseDelay
                    : helpersUnlockedAt >= 0f && elapsed >= helpersUnlockedAt + ReadyDelay + ghost.ReleaseDelay;
            if (!ghost.Released && canLeave)
            {
                ghost.Released = true;
                ghost.DesiredDirection = MazeDirection.Up;
            }
        }
    }

    /// <summary>Places a limited trap fruit at the selected ghost cell.</summary>
    private void TryDropTrap(Ghost owner)
    {
        if (!owner.Alive || !owner.Released || owner.TrapFruitsRemaining <= 0)
            return;
        if (!maze.IsChomperWalkable(owner.Cell))
            return;
        if (traps.Any(trap => trap.Cell == owner.Cell))
            return;

        traps.Add(new TrapFruit(owner.Cell));
        owner.TrapFruitsRemaining--;
    }

    /// <summary>Reads player two and three gamepads and drives their local co-op ghosts.</summary>
    private void HandleAdditionalPlayerInput(GamePadState gamePadTwo, GamePadState gamePadThree)
    {
        if (IsMobile) return;
        HandlePlayerGhostInput(1, gamePadTwo);
        HandlePlayerGhostInput(2, gamePadThree);
    }

    /// <summary>Applies movement and trap input from one local co-op player.</summary>
    private void HandlePlayerGhostInput(int ghostIndex, GamePadState gamePad)
    {
        if (ghostIndex >= playerCount || ghostIndex >= ghosts.Count || !ghosts[ghostIndex].Alive) return;
        var direction = ReadGamePadDirection(gamePad);
        if (direction != MazeDirection.None) ghosts[ghostIndex].DesiredDirection = direction;
        if (gamePad.IsButtonDown(trapButton)) TryDropTrap(ghosts[ghostIndex]);
    }

    /// <summary>Maps a gamepad D-pad or left stick vector to a maze direction.</summary>
    /// <summary>Maps D-pad or left-stick input to a maze direction.</summary>
    private static MazeDirection ReadGamePadDirection(GamePadState pad)
    {
        if (pad.DPad.Up == ButtonState.Pressed || pad.ThumbSticks.Left.Y > .35f) return MazeDirection.Up;
        if (pad.DPad.Right == ButtonState.Pressed || pad.ThumbSticks.Left.X > .35f) return MazeDirection.Right;
        if (pad.DPad.Down == ButtonState.Pressed || pad.ThumbSticks.Left.Y < -.35f) return MazeDirection.Down;
        if (pad.DPad.Left == ButtonState.Pressed || pad.ThumbSticks.Left.X < -.35f) return MazeDirection.Left;
        return MazeDirection.None;
    }

    /// <summary>Reads the primary player's platform-appropriate movement input.</summary>
    private MazeDirection ReadPlayerDirection()
    {
        if (swipeDirection != MazeDirection.None)
        {
            var direction = swipeDirection;
            swipeDirection = MazeDirection.None;
            return direction;
        }

        if (IsMobile) return MazeDirection.None;

        if (Keyboard.GetState().IsKeyDown(Keys.W) || Keyboard.GetState().IsKeyDown(Keys.Up)) return MazeDirection.Up;
        if (Keyboard.GetState().IsKeyDown(Keys.D) || Keyboard.GetState().IsKeyDown(Keys.Right)) return MazeDirection.Right;
        if (Keyboard.GetState().IsKeyDown(Keys.S) || Keyboard.GetState().IsKeyDown(Keys.Down)) return MazeDirection.Down;
        if (Keyboard.GetState().IsKeyDown(Keys.A) || Keyboard.GetState().IsKeyDown(Keys.Left)) return MazeDirection.Left;

        return ReadGamePadDirection(GamePad.GetState(PlayerIndex.One));
    }

    /// <summary>Converts a swipe delta into its dominant cardinal direction.</summary>
    private static MazeDirection FromVector(Vector2 delta) => Math.Abs(delta.X) > Math.Abs(delta.Y)
        ? (delta.X >= 0 ? MazeDirection.Right : MazeDirection.Left)
        : (delta.Y >= 0 ? MazeDirection.Down : MazeDirection.Up);

    /// <summary>Returns the direction that would immediately reverse movement.</summary>
    private static MazeDirection Opposite(MazeDirection direction) => direction switch
    {
        MazeDirection.Up => MazeDirection.Down,
        MazeDirection.Right => MazeDirection.Left,
        MazeDirection.Down => MazeDirection.Up,
        MazeDirection.Left => MazeDirection.Right,
        _ => MazeDirection.None
    };

    /// <summary>Trains the ghost policy from the primary player's latest action.</summary>
    private void LearnFromPlayer()
    {
        if (!Selected.Released || Selected.DesiredDirection == MazeDirection.None) return;
        var stateKey = BuildState(Selected.Cell, NearestChomper(Selected.Cell));
        var next = Selected.Cell + Selected.DesiredDirection.Offset();
        var reward = maze.IsWalkable(next) ? .2f : -.35f;
        ghostPolicy.Learn(stateKey, Selected.DesiredDirection, reward,
            BuildState(next, NearestChomper(next)));
        if (previousPlayerCell != Selected.Cell) previousPlayerCell = Selected.Cell;
    }

    /// <summary>Moves the controlled ghost, co-op ghost, and algorithmic NPC helpers.</summary>
    private void UpdateGhosts(float delta)
    {
        foreach (var ghost in ghosts)
        {
            if (!ghost.Alive || !ghost.Released) continue;
            if (ghost != Selected && ghost.Player is null && !ghost.IsMoving)
            {
                var target = NearestChomper(ghost.Cell);
                var strategy = ghost.Algorithm switch
                {
                    NavigationAlgorithm.AStar => MazePathfinding.AStar(maze, ghost.Cell, target),
                    NavigationAlgorithm.TravellerSalesman => TspStep(ghost),
                    NavigationAlgorithm.FiniteStateMachine => MazePathfinding.AStar(maze, ghost.Cell,
                        Manhattan(ghost.Cell, target) <= 8 ? target : maze.GhostHouseCenter),
                    _ => MazeDirection.None
                };
                var learned = ghostPolicy.Predict(BuildState(ghost.Cell, target), random, .04f);
                ghost.DesiredDirection = random.NextDouble() < .22 && CanMove(ghost.Cell, learned)
                    ? learned : strategy;
            }
            MoveActor(ghost, delta, GhostSpeed * (powerFruitTime > 0 ? .55f : 1f));
        }
    }

    /// <summary>Computes the nearest-target step used by the TSP strategy.</summary>
    private MazeDirection TspStep(Ghost ghost)
    {
        var target = chompers.Where(p => p.Alive).OrderBy(p => Manhattan(ghost.Cell, p.Cell)).FirstOrDefault();
        return target == null ? MazeDirection.None : MazePathfinding.AStar(maze, ghost.Cell, target.Cell);
    }

    /// <summary>Updates every Chomper clone and alternates its navigation policy.</summary>
    private void UpdateChompers(float delta)
    {
        foreach (var chomper in chompers.Where(p => p.Alive).ToList())
        {
            chomper.PowerTime = Math.Max(0f, chomper.PowerTime - delta);
            chomper.TrapTime = Math.Max(0f, chomper.TrapTime - delta);
            if (!chomper.IsMoving)
            {
                if (chomper.PelletsSinceAlgorithmChange >= AlgorithmSwitchEveryPellets)
                {
                    chomperAlgorithm = (NavigationAlgorithm)random.Next(3);
                    chomper.PelletsSinceAlgorithmChange = 0;
                }
                var trap = traps.Where(item => maze.IsChomperWalkable(item.Cell))
                    .OrderBy(item => Manhattan(chomper.Cell, item.Cell)).FirstOrDefault();
                var target = ghosts.Where(g => g.Alive).OrderBy(g => Manhattan(chomper.Cell, g.Cell)).FirstOrDefault();
                Point targetCell;
                if (chomper.PowerTime > 0f && target != null)
                {
                    chomper.HasObjective = false;
                    targetCell = target.Cell;
                }
                else if (trap != null)
                {
                    chomper.HasObjective = false;
                    targetCell = trap.Cell;
                }
                else
                {
                    var reserved = chompers
                        .Where(other => !ReferenceEquals(other, chomper) && other.HasObjective)
                        .Select(other => other.ObjectiveCell)
                        .ToHashSet();
                    if (!chomper.HasObjective || !HasChomperCollectible(chomper.ObjectiveCell)
                        || reserved.Contains(chomper.ObjectiveCell))
                    {
                        chomper.ObjectiveCell = SelectChomperObjective(chomper, reserved);
                        chomper.HasObjective = HasChomperCollectible(chomper.ObjectiveCell);
                    }
                    targetCell = chomper.ObjectiveCell;
                }
                if (!maze.IsChomperWalkable(targetCell)) targetCell = maze.ClosestChomperCell(targetCell);
                chomper.DesiredDirection = MazePathfinding.AStar(maze, chomper.Cell, targetCell, chomperRoute: true);
                if (chomper.PowerTime > 0f && target != null && chomperAlgorithm == NavigationAlgorithm.TravellerSalesman)
                    chomper.DesiredDirection = MazePathfinding.AStar(maze, chomper.Cell,
                        maze.ClosestChomperCell(Selected.Cell), chomperRoute: true);
                var learned = chomperPolicy.Predict(BuildState(chomper.Cell, targetCell), random, .08f);
                if (CanMoveForActor(chomper, learned) && random.NextDouble() < .05
                    && learned != Opposite(chomper.Direction)
                    && Manhattan(chomper.Cell + learned.Offset(), targetCell) < Manhattan(chomper.Cell, targetCell))
                    chomper.DesiredDirection = learned;
            }
            var speed = chomper.TrapTime > 0f ? TrappedChomperSpeed : ChomperSpeed;
            MoveActor(chomper, delta, speed);
        }
    }

    /// <summary>Ends the phase immediately when any Chomper consumes the final pebble.</summary>
    private void EvaluatePelletGameOver()
    {
        if (state == GameState.Playing && maze.RemainingPelletCount() <= 0)
            state = GameState.GameOver;
    }

    /// <summary>Checks whether a cell still contains a Chomper objective.</summary>
    private bool HasChomperCollectible(Point cell) => maze.HasPellet(cell) || maze.HasPowerFruit(cell);

    /// <summary>Selects a stable, non-reserved objective with coverage preference.</summary>
    private Point SelectChomperObjective(Chomper chomper, ISet<Point> reserved)
    {
        var candidates = maze.WalkableCells()
            .Where(HasChomperCollectible)
            .Where(cell => !reserved.Contains(cell))
            .ToList();
        var outsideHouse = candidates
            .Where(cell => Manhattan(cell, maze.GhostHouseCenter) > 4)
            .ToList();
        if (outsideHouse.Count > 0) candidates = outsideHouse;

        var objective = candidates
            .Select(cell => new
            {
                Cell = cell,
                Score = Manhattan(chomper.Cell, cell)
                    + (chomper.RecentCells.Contains(cell) ? 16 : 0)
                    - Math.Min(8f, Manhattan(cell, maze.GhostHouseCenter) * .35f)
            })
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Cell.Y)
            .ThenBy(item => item.Cell.X)
            .FirstOrDefault();
        return objective?.Cell ?? chomper.Cell;
    }

    /// <summary>Moves an actor one grid edge at the requested speed.</summary>
    private void MoveActor(Actor actor, float delta, float speed)
    {
        if (!actor.IsMoving)
        {
            if (CanMoveForActor(actor, actor.DesiredDirection)) actor.Direction = actor.DesiredDirection;
            else if (!CanMoveForActor(actor, actor.Direction)) actor.Direction = MazeDirection.None;
            if (CanMoveForActor(actor, actor.Direction))
            {
                actor.TargetCell = actor.Cell + actor.Direction.Offset();
                actor.IsMoving = true;
            }
        }
        if (!actor.IsMoving) return;
        var offset = CellToWorld(actor.TargetCell) - actor.Position;
        var distance = offset.Length();
        var travel = speed * delta;
        if (distance <= travel)
        {
            actor.Position = CellToWorld(actor.TargetCell);
            actor.Cell = actor.TargetCell;
            actor.IsMoving = false;
            if (actor is Chomper chomper)
            {
                chomper.RecentCells.Enqueue(chomper.Cell);
                while (chomper.RecentCells.Count > 12) chomper.RecentCells.Dequeue();
            }
        }
        else actor.Position += Vector2.Normalize(offset) * travel;
    }

    /// <summary>Collects pellets, power fruit, and trap fruit for live Chompers.</summary>
    private void CollectChomperItems()
    {
        foreach (var chomper in chompers.Where(p => p.Alive).ToList())
        {
            if (maze.ConsumePellet(chomper.Cell))
            {
                chomper.PelletsSinceAlgorithmChange++;
                pelletsCollected = pelletCount - maze.RemainingPelletCount();
                chomper.HasObjective = false;
            }
            if (maze.ConsumePowerFruit(chomper.Cell))
            {
                powerFruitTime = PowerFruitDuration;
                chomper.PowerTime = PowerFruitDuration;
                chomper.HasObjective = false;
                if (chompers.Count == 1)
                {
                    var cloneStart = maze.ChomperStarts.Count > 1 ? maze.ChomperStarts[1] : maze.ChomperStart;
                    var clone = new Chomper(RetroPalette.Orange, cloneStart)
                    {
                        Position = CellToWorld(cloneStart),
                        DesiredDirection = MazeDirection.Left,
                        PowerTime = PowerFruitDuration
                    };
                    chompers.Add(clone);
                }
            }

            var trap = traps.FirstOrDefault(item => item.Cell == chomper.Cell);
            if (trap != null)
            {
                traps.Remove(trap);
                chomper.TrapTime = TrapDuration;
            }
        }
    }

    /// <summary>Resolves vulnerable captures and non-vulnerable ghost losses.</summary>
    private void ResolveCollisions()
    {
        foreach (var ghost in ghosts.Where(g => g.Alive && g.Released).ToList())
        foreach (var chomper in chompers.Where(p => p.Alive).ToList())
        {
            if (Vector2.Distance(ghost.Position, chomper.Position) > TileSize * .32f) continue;
            var relative = chomper.Cell - ghost.Cell;
            var canCapture = relative == Point.Zero || ghost.Direction == MazeDirectionExtensions.FromDelta(relative);
            if (canCapture && chomper.TrapTime > 0f)
            {
                CaptureChomperLife();
                return;
            }
            else if (chomper.PowerTime > 0f)
            {
                ghost.Alive = false;
                ghost.Released = false;
                if (ReferenceEquals(ghost, Selected)) TransferControlAfterDeath();
                if (ghosts.All(defeatedGhost => !defeatedGhost.Alive))
                {
                    allGhostsDefeated = true;
                    state = GameState.GameOver;
                    return;
                }
            }
        }
    }

    /// <summary>Consumes one Chomper life while preserving already eaten pebbles.</summary>
    private void CaptureChomperLife()
    {
        chompersCaptured++;
        chomperLives--;
        score += maze.RemainingPelletCount() * 10;

        foreach (var oldChomper in chompers) oldChomper.Alive = false;
        traps.Clear();
        powerFruitTime = 0f;
        pelletsCollected = pelletCount - maze.RemainingPelletCount();

        if (chomperLives <= 0)
        {
            state = GameState.Won;
            return;
        }

        chompers.Clear();
        var chomper = new Chomper(RetroPalette.Red, maze.ChomperStarts[0])
        {
            Position = CellToWorld(maze.ChomperStarts[0])
        };
        chompers.Add(chomper);

        for (var i = 0; i < ghosts.Count; i++)
        {
            var ghost = ghosts[i];
            if (!ghost.Alive) continue;
            ghost.Cell = maze.GhostStarts[i];
            ghost.Position = CellToWorld(ghost.Cell);
            ghost.TargetCell = ghost.Cell;
            ghost.Direction = MazeDirection.Up;
            ghost.DesiredDirection = MazeDirection.Up;
            ghost.IsMoving = false;
            ghost.Released = i == 0 || helpersUnlockedAt >= 0f;
        }
    }

    /// <summary>Chooses a living replacement when the selected ghost is captured.</summary>
    private void TransferControlAfterDeath()
    {
        var replacement = ghosts
            .Select((ghost, index) => (ghost, index))
            .Where(item => item.ghost.Alive)
            .OrderBy(item => item.ghost.Released ? 0 : 1)
            .Select(item => item.index)
            .FirstOrDefault(-1);
        if (replacement >= 0)
        {
            selectedGhost = replacement;
            ghosts[replacement].Released = true;
            ghosts[replacement].DesiredDirection = MazeDirection.Up;
            ghosts[replacement].IsMoving = false;
            previousPlayerCell = ghosts[replacement].Cell;
        }
    }

    /// <summary>Finds the closest live Chomper cell to an origin cell.</summary>
    private Point NearestChomper(Point origin)
    {
        var result = chompers.Where(p => p.Alive).OrderBy(p => Manhattan(origin, p.Cell)).FirstOrDefault();
        return result?.Cell ?? maze.ChomperStart;
    }

    /// <summary>Returns Manhattan distance between two grid cells.</summary>
    private static int Manhattan(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    /// <summary>Checks whether a direction leads to a traversable cell.</summary>
    private bool CanMove(Point cell, MazeDirection direction) => direction != MazeDirection.None
        && maze.IsWalkable(cell + direction.Offset());

    /// <summary>Checks movement using the actor-specific ghost-house access rule.</summary>
    private bool CanMoveForActor(Actor actor, MazeDirection direction) => direction != MazeDirection.None
        && (actor is Chomper ? maze.IsChomperWalkable(actor.Cell + direction.Offset()) :
            maze.IsWalkable(actor.Cell + direction.Offset()));

    /// <summary>Builds the compact state key shared by online learning policies.</summary>
    private string BuildState(Point cell, Point target)
    {
        var walls = string.Concat(
            CanMove(cell, MazeDirection.Up) ? '0' : '1',
            CanMove(cell, MazeDirection.Right) ? '0' : '1',
            CanMove(cell, MazeDirection.Down) ? '0' : '1',
            CanMove(cell, MazeDirection.Left) ? '0' : '1');
        return $"{cell.X},{cell.Y}|{target.X},{target.Y}|{walls}";
    }

    /// <summary>Converts a maze cell to a logical pixel position.</summary>
    private Vector2 CellToWorld(Point cell) => new(OriginX + cell.X * TileSize + TileSize / 2f,
        OriginY + cell.Y * TileSize + TileSize / 2f);

    /// <summary>Converts a physical touch coordinate to the fixed logical game canvas.</summary>
    private Vector2 ToLogical(Vector2 screenPosition) => new(
        screenPosition.X * LogicalWidth / Math.Max(1f, GraphicsDevice.Viewport.Width),
        screenPosition.Y * LogicalHeight / Math.Max(1f, GraphicsDevice.Viewport.Height));

    /// <summary>Returns the non-filtered pixel-art scale used to fill every viewport.</summary>
    private Matrix PresentationTransform() => Matrix.CreateScale(
        GraphicsDevice.Viewport.Width / (float)LogicalWidth,
        GraphicsDevice.Viewport.Height / (float)LogicalHeight,
        1f);

    /// <summary>Draws gameplay or the localized front-end using the same logical canvas.</summary>
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(RetroPalette.Ink);
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            transformMatrix: PresentationTransform());
        if (state is GameState.Menu or GameState.Difficulty or GameState.Options or GameState.Controls or GameState.PauseConfirmation)
            DrawFrontEnd();
        else
        {
            DrawMaze();
            DrawTraps();
            foreach (var chomper in chompers.Where(p => p.Alive)) DrawChomper(chomper);
            foreach (var ghost in ghosts.Where(g => g.Alive)) DrawGhost(ghost, ghost == Selected);
            DrawHud();
        }
        spriteBatch.End();
        base.Draw(gameTime);
    }

    /// <summary>Draws menu, difficulty, options, and desktop control configuration screens.</summary>
    private void DrawFrontEnd()
    {
        Rect(0, 0, LogicalWidth, LogicalHeight, RetroPalette.Ink);
        Rect(104, 42, 592, 476, RetroPalette.DeepNavy);
        Rect(108, 46, 584, 468, RetroPalette.Wall);
        Rect(112, 50, 576, 460, RetroPalette.Ink);
        spriteBatch.DrawString(hudFont, GameText.Get("GameTitle"), new Vector2(278, 78), RetroPalette.Yellow);

        if (state == GameState.Menu)
        {
            spriteBatch.DrawString(hudFont, GameText.Get("MenuTitle"), new Vector2(334, 122), RetroPalette.White);
            var labels = new[] { "MenuOnePlayer", "MenuTwoPlayers", "MenuThreePlayers", "MenuOptions", "MenuQuit" };
            for (var i = 0; i < labels.Length; i++)
                DrawMenuRow(GameText.Get(labels[i]), 170 + i * 42, i == menuSelection);
            spriteBatch.DrawString(hudFont, GameText.Get(IsMobile ? "ControlsMobile" : "ControlsDesktop"),
                new Vector2(170, 420), RetroPalette.Gray);
            return;
        }

        if (state == GameState.Difficulty)
        {
            spriteBatch.DrawString(hudFont, GameText.Format("DifficultyForPlayers", playerCount),
                new Vector2(280, 122), RetroPalette.White);
            DrawMenuRow(GameText.Get("DifficultyEasy"), 184, difficultySelection == 0);
            DrawMenuRow(GameText.Get("DifficultyNormal"), 226, difficultySelection == 1);
            DrawMenuRow(GameText.Get("DifficultyHard"), 268, difficultySelection == 2);
            DrawMenuRow(GameText.Get("Back"), 310, difficultySelection == 3);
        }
        else if (state == GameState.Options)
        {
            spriteBatch.DrawString(hudFont, GameText.Get("OptionsTitle"), new Vector2(334, 122), RetroPalette.White);
            DrawMenuRow(GameText.Format("OptionsHints", hintsEnabled ? GameText.Get("OptionsOn") : GameText.Get("OptionsOff")),
                184, optionsSelection == 0);
            DrawMenuRow(GameText.Format("OptionsFullscreen", fullscreenEnabled ? GameText.Get("OptionsOn") : GameText.Get("OptionsOff")),
                226, optionsSelection == 1);
            DrawMenuRow(GameText.Get("MenuControls"), 268, optionsSelection == 2);
            DrawMenuRow(GameText.Get("Back"), 310, optionsSelection == 3);
            spriteBatch.DrawString(hudFont, GameText.Get("OptionsLanguage"), new Vector2(208, 390), RetroPalette.Gray);
        }
        else if (state == GameState.Controls)
        {
            spriteBatch.DrawString(hudFont, GameText.Get("ControlsTitle"), new Vector2(302, 122), RetroPalette.White);
            DrawMenuRow(GameText.Format("ControlsKeyboardTrap", trapKey), 160, controlsSelection == 0);
            DrawMenuRow(GameText.Format("ControlsGamepadGhostOne", ghostOneButton), 198, controlsSelection == 1);
            DrawMenuRow(GameText.Format("ControlsGamepadGhostTwo", ghostTwoButton), 236, controlsSelection == 2);
            DrawMenuRow(GameText.Format("ControlsGamepadGhostThree", ghostThreeButton), 274, controlsSelection == 3);
            DrawMenuRow(GameText.Format("ControlsGamepadTrap", trapButton), 312, controlsSelection == 4);
            DrawMenuRow(GameText.Format("ControlsGamepadPause", pauseButton), 350, controlsSelection == 5);
            DrawMenuRow(GameText.Get("Back"), 388, controlsSelection == 6);
            spriteBatch.DrawString(hudFont, GameText.Get("ControlsMove"), new Vector2(180, 426), RetroPalette.Gray);
            var prompt = waitingForControl ? GameText.Get("ControlsPressKey") :
                waitingForGamepad ? GameText.Get("ControlsAssignButton") : GameText.Get("ControlsConfigure");
            spriteBatch.DrawString(hudFont, prompt, new Vector2(150, 462),
                waitingForControl || waitingForGamepad ? RetroPalette.Yellow : RetroPalette.Gray);
        }
        else
        {
            spriteBatch.DrawString(hudFont, GameText.Get("PauseTitle"), new Vector2(350, 122), RetroPalette.Yellow);
            spriteBatch.DrawString(hudFont, GameText.Get("PauseQuestion"), new Vector2(205, 158), RetroPalette.White);
            DrawMenuRow(GameText.Get("PauseSave"), 220, pauseSelection == 0);
            DrawMenuRow(GameText.Get("PauseDiscard"), 262, pauseSelection == 1);
            DrawMenuRow(GameText.Get("PauseCancel"), 304, pauseSelection == 2);
        }
    }

    /// <summary>Draws one selectable front-end row with an arcade-style cursor.</summary>
    private void DrawMenuRow(string label, int y, bool selected)
    {
        if (selected) Rect(220, y - 4, 360, 30, RetroPalette.Purple);
        spriteBatch.DrawString(hudFont, (selected ? "> " : "  ") + label, new Vector2(250, y),
            selected ? RetroPalette.Yellow : RetroPalette.White);
    }

    /// <summary>Draws runtime-generated walls, pellets, fruit, and ghost house.</summary>
    private void DrawMaze()
    {
        for (var y = 0; y < ProceduralMaze.Height; y++)
        for (var x = 0; x < ProceduralMaze.Width; x++)
        {
            var cell = new Point(x, y);
            var rect = new Rectangle(OriginX + x * TileSize, OriginY + y * TileSize, TileSize, TileSize);
            if (!maze.IsWalkable(cell))
            {
                Rect(rect.X, rect.Y, rect.Width, rect.Height, RetroPalette.DeepNavy);
                Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, 2, RetroPalette.Wall);
                Rect(rect.X + 2, rect.Y + 2, 2, rect.Height - 4, RetroPalette.Wall);
            }
            else if (maze.HasPellet(cell))
                Rect(rect.Center.X - 2, rect.Center.Y - 2, 4, 4, RetroPalette.White);
            if (maze.HasPowerFruit(cell))
            {
                Rect(rect.Center.X - 5, rect.Center.Y - 4, 10, 8, RetroPalette.Pink);
                Rect(rect.Center.X - 2, rect.Center.Y - 7, 4, 3, RetroPalette.Green);
            }
        }

        // The border is visual only. The generated ring remains walkable, so
        // Chomper can route around the house without entering it.
        var house = new Rectangle(OriginX + (maze.GhostHouseCenter.X - 2) * TileSize,
            OriginY + (maze.GhostHouseCenter.Y - 1) * TileSize, TileSize * 5, TileSize * 3);
        Rect(house.X, house.Y, house.Width, 2, RetroPalette.Purple);
        Rect(house.X, house.Bottom - 2, house.Width, 2, RetroPalette.Purple);
        Rect(house.X, house.Y, 2, house.Height, RetroPalette.Purple);
        Rect(house.Right - 2, house.Y, 2, house.Height, RetroPalette.Purple);
    }

    /// <summary>Draws a ghost from palette rectangles, including selection marker.</summary>
    private void DrawGhost(Ghost ghost, bool selected)
    {
        var x = (int)ghost.Position.X;
        var y = (int)ghost.Position.Y;
        var color = powerFruitTime > 0 ? RetroPalette.PowerBlue : ghost.Color;
        Rect(x - 9, y - 8, 18, 17, color);
        Rect(x - 7, y - 11, 14, 5, color);
        Rect(x - 9, y + 7, 5, 4, color);
        Rect(x - 2, y + 7, 5, 4, color);
        Rect(x + 5, y + 7, 4, 4, color);
        Rect(x - 6, y - 5, 5, 6, RetroPalette.White);
        Rect(x + 2, y - 5, 5, 6, RetroPalette.White);
        Rect(x - 4, y - 3, 2, 3, RetroPalette.Black);
        Rect(x + 4, y - 3, 2, 3, RetroPalette.Black);
        if (selected) Rect(x - 12, y - 16, 24, 2, RetroPalette.White);
    }

    /// <summary>Draws every active trap fruit as pixel-art geometry.</summary>
    private void DrawTraps()
    {
        foreach (var trap in traps)
        {
            var center = CellToWorld(trap.Cell);
            Rect((int)center.X - 6, (int)center.Y - 5, 12, 10, RetroPalette.Orange);
            Rect((int)center.X - 3, (int)center.Y - 8, 6, 3, RetroPalette.Yellow);
            Rect((int)center.X - 2, (int)center.Y - 2, 4, 4, RetroPalette.Black);
        }
    }

    /// <summary>Draws a red or orange Chomper clone without external images.</summary>
    private void DrawChomper(Chomper chomper)
    {
        var x = (int)chomper.Position.X;
        var y = (int)chomper.Position.Y;
        Rect(x - 8, y - 8, 16, 16, chomper.Color);
        Rect(x - 5, y - 11, 10, 22, chomper.Color);
        Rect(x - 11, y - 5, 22, 10, chomper.Color);
        var mouth = chomper.Direction == MazeDirection.Left ? new Rectangle(x - 10, y - 2, 7, 4) :
            chomper.Direction == MazeDirection.Up ? new Rectangle(x - 2, y - 10, 4, 7) :
            chomper.Direction == MazeDirection.Down ? new Rectangle(x - 2, y + 3, 4, 7) :
            new Rectangle(x + 3, y - 2, 7, 4);
        spriteBatch.Draw(pixel, mouth, RetroPalette.Ink);
    }

    /// <summary>Draws the localized gameplay HUD using the Array sprite font.</summary>
    private void DrawHud()
    {
        Rect(0, 0, LogicalWidth, OriginY - 2, RetroPalette.Ink);
        spriteBatch.DrawString(hudFont,
            GameText.Format("HudTop", phase + 1, maze.RemainingPelletCount(), score),
            new Vector2(10, 6), RetroPalette.White);

        var helperStatus = helpersUnlockedAt < 0f
            ? GameText.Format("HudHelpersWait", releaseThreshold)
            : GameText.Get("HudHelpersReady");
        spriteBatch.DrawString(hudFont,
            GameText.Format("HudStats", chomperLives, chompersCaptured, helperStatus),
            new Vector2(10, 28), RetroPalette.Gray);

        for (var i = 0; i < ghosts.Count; i++)
        {
            var ghost = ghosts[i];
            var color = ghost.Alive ? ghost.Color : RetroPalette.Gray;
            var x = 10 + i * 58;
            Rect(x, 52, 14, 10, color);
            spriteBatch.DrawString(hudFont, (i + 1).ToString(), new Vector2(x + 18, 47),
                i == selectedGhost ? RetroPalette.White : color);
        }

        var status = powerFruitTime > 0 ? GameText.Format("HudPower", powerFruitTime) :
            GameText.Get(IsMobile ? "HudMobile" : "HudDesktop");
        spriteBatch.DrawString(hudFont, status, new Vector2(190, 48),
            powerFruitTime > 0 ? RetroPalette.Pink : RetroPalette.Gray);
        spriteBatch.DrawString(hudFont, GameText.Format("HudTraps", Selected.TrapFruitsRemaining),
            new Vector2(630, 28), RetroPalette.Orange);
        if (state == GameState.GameOver)
            DrawMessageDialog(allGhostsDefeated ? "GameOverGhosts" : "GameOverPellets", RetroPalette.Red, true);
        else if (state == GameState.Won)
            DrawMessageDialog("WonCapture", RetroPalette.Green, true);
        else if (elapsed < ReadyDelay && hintsEnabled)
            DrawMessageDialog("ReadyMessage", RetroPalette.Yellow, false);
    }

    /// <summary>Draws an arcade-styled message box over the gameplay canvas.</summary>
    private void DrawMessageDialog(string messageKey, Color accent, bool showContinue)
    {
        var box = showContinue
            ? new Rectangle(132, 214, 536, 132)
            : new Rectangle(190, 236, 420, 74);
        Rect(box.X + 6, box.Y + 6, box.Width, box.Height, RetroPalette.Black);
        Rect(box.X, box.Y, box.Width, box.Height, RetroPalette.Wall);
        Rect(box.X + 4, box.Y + 4, box.Width - 8, box.Height - 8, RetroPalette.Ink);
        Rect(box.X + 8, box.Y + 8, box.Width - 16, 3, accent);
        Rect(box.X + 8, box.Bottom - 11, box.Width - 16, 3, accent);

        var message = GameText.Get(messageKey);
        var maxWidth = box.Width - 34;
        var measured = hudFont.MeasureString(message);
        var scale = Math.Min(1f, maxWidth / Math.Max(1f, measured.X));
        var messageSize = measured * scale;
        var messagePosition = new Vector2(
            box.Center.X - messageSize.X / 2f,
            box.Y + (showContinue ? 30 : 28));
        spriteBatch.DrawString(hudFont, message, messagePosition, accent, 0f, Vector2.Zero,
            scale, SpriteEffects.None, 0f);

        if (showContinue)
        {
            var continueText = GameText.Get("DialogContinue");
            var continueSize = hudFont.MeasureString(continueText);
            spriteBatch.DrawString(hudFont, continueText,
                new Vector2(box.Center.X - continueSize.X / 2f, box.Bottom - 34), RetroPalette.White);
        }
    }

    /// <summary>Draws one solid pixel-art rectangle using the generated texture.</summary>
    private void Rect(int x, int y, int width, int height, Color color) =>
        spriteBatch.Draw(pixel, new Rectangle(x, y, width, height), color);

    /// <summary>Releases runtime textures and sprite batching resources.</summary>
    protected override void UnloadContent()
    {
        pixel?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }
}
