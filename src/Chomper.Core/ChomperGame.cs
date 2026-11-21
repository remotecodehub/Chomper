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
/// Reversed Pac-Man gameplay: the player controls one of three ghosts and the
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
    private const int PacmanLives = 3;
    private const int TrapFruitsPerGhost = 3;
    private const int AlgorithmSwitchEveryPellets = 8;

    private readonly GraphicsDeviceManager graphics;
    private readonly Random random = new(51937);
    private readonly OnlineLearningPolicy ghostPolicy = new();
    private readonly OnlineLearningPolicy pacmanPolicy = new();
    private readonly List<Ghost> ghosts = new();
    private readonly List<Pacman> pacmen = new();
    private readonly List<TrapFruit> traps = new();

    private SpriteBatch spriteBatch;
    private Texture2D pixel;
    private SpriteFont hudFont;
    private ProceduralMaze maze;
    private KeyboardState previousKeyboard;
    private GamePadState previousGamePad;
    private GamePadState previousGamePadTwo;
    private int phase;
    private int selectedGhost;
    private int score;
    private int pelletsCollected;
    private int pelletCount;
    private int pacmanLives;
    private int pacmenCaptured;
    private int releaseThreshold;
    private int nextCheckpointPellets;
    private float helpersUnlockedAt = -1f;
    private HashSet<Point> checkpointRemainingPellets = new();
    private float elapsed;
    private float powerFruitTime;
    private NavigationAlgorithm pacmanAlgorithm = NavigationAlgorithm.AStar;
    private GameState state;
    private Point previousPlayerCell;
    private MazeDirection swipeDirection;
    private int menuSelection;
    private int difficultySelection = 1;
    private int optionsSelection;
    private bool hintsEnabled = true;
    private bool fullscreenEnabled;
    private bool waitingForControl;
    private bool twoPlayers;
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
    /// <summary>Returns Pac-Man's normal chase speed relative to the ghosts.</summary>
    private float PacmanSpeed => GhostSpeed * 1.75f;
    /// <summary>Returns Pac-Man's speed while affected by a trap fruit.</summary>
    private float TrappedPacmanSpeed => GhostSpeed * .5f;

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

    private sealed class Pacman : Actor
    {
        public Color Color { get; }
        public int PelletsSinceAlgorithmChange;
        public float PowerTime;
        public float TrapTime;

        /// <summary>Indicates whether a power fruit or trap currently permits capture.</summary>
        public bool IsVulnerable => PowerTime > 0f || TrapTime > 0f;

        /// <summary>Creates a Pac-Man actor at a maze cell.</summary>
        public Pacman(Color color, Point cell) : base(cell) => Color = color;
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
        LocalizationManager.SetCulture(LocalizationManager.DEFAULT_CULTURE_CODE);
        TouchPanel.EnabledGestures = GestureType.Flick | GestureType.Tap | GestureType.DoubleTap | GestureType.Hold;
        base.Initialize();
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
        pacmen.Clear();
        traps.Clear();
        ghosts.Add(new Ghost("VERDE", RetroPalette.Green, NavigationAlgorithm.AStar,
            maze.GhostStarts[0], 0f));
        ghosts.Add(new Ghost("AMARELO", RetroPalette.Yellow, NavigationAlgorithm.TravellerSalesman,
            maze.GhostStarts[1], 2.5f));
        ghosts.Add(new Ghost("AZUL", RetroPalette.Blue, NavigationAlgorithm.FiniteStateMachine,
            maze.GhostStarts[2], 4.5f));
        ghosts[0].Player = PlayerIndex.One;
        ghosts[1].Player = twoPlayers ? PlayerIndex.Two : null;
        foreach (var ghost in ghosts) ghost.Position = CellToWorld(ghost.Cell);

        pacmen.Add(new Pacman(RetroPalette.Red, maze.PacmanStart));
        pacmen[0].Position = CellToWorld(maze.PacmanStart);
        pelletCount = maze.WalkableCells().Count(maze.HasPellet);
        checkpointRemainingPellets = maze.RemainingPellets();
        releaseThreshold = Math.Max(1, (int)Math.Ceiling(pelletCount * .25f));
        nextCheckpointPellets = releaseThreshold;
        helpersUnlockedAt = -1f;
        pacmanLives = PacmanLives;
        pacmenCaptured = 0;
        selectedGhost = 0;
        score = 0;
        pelletsCollected = 0;
        elapsed = 0f;
        powerFruitTime = 0f;
        pacmanAlgorithm = NavigationAlgorithm.AStar;
        swipeDirection = MazeDirection.None;
        previousGamePad = default;
        previousGamePadTwo = default;
        state = GameState.Playing;
        previousPlayerCell = Selected.Cell;
    }

    /// <summary>Advances menus, input, actors, collectibles, and collision rules.</summary>
    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        var gamePad = IsMobile ? default : GamePad.GetState(PlayerIndex.One);
        var gamePadTwo = IsMobile ? default : GamePad.GetState(PlayerIndex.Two);
        if (!IsMobile && (keyboard.IsKeyDown(Keys.Escape) || gamePad.Buttons.Back == ButtonState.Pressed))
        {
            Exit();
            return;
        }

        if (state is GameState.Menu or GameState.Difficulty or GameState.Options or GameState.Controls)
        {
            UpdateFrontEnd(keyboard, gamePad);
            previousKeyboard = keyboard;
            previousGamePad = gamePad;
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
        HandleSecondPlayerInput(gamePadTwo);
        ReleaseGhosts();

        Selected.DesiredDirection = ReadPlayerDirection();
        LearnFromPlayer();
        UpdateGhosts(delta);
        UpdatePacmen(delta);
        CollectPacmanItems();
        ResolveCollisions();

        pelletsCollected = pelletCount - maze.RemainingPelletCount();
        if (state == GameState.Playing && maze.RemainingPelletCount() == 0)
            state = GameState.GameOver;

        previousKeyboard = keyboard;
        previousGamePad = gamePad;
        previousGamePadTwo = gamePadTwo;
        Window.Title = GameText.Format("WindowTitlePlaying", phase + 1, score, pacmanLives);
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
                break;
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
             (gamePad.IsButtonDown(Buttons.B) && previousGamePad.IsButtonUp(Buttons.B)));

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
            GameState.Menu => IsMobile ? 4 : 6,
            GameState.Difficulty => 3,
            GameState.Options => 3,
            GameState.Controls => 3,
            _ => 1
        };
        if (state == GameState.Menu)
            menuSelection = (menuSelection + offset + count) % count;
        else if (state == GameState.Difficulty)
            difficultySelection = (difficultySelection + offset + count) % count;
        else
            optionsSelection = (optionsSelection + offset + count) % count;
    }

    /// <summary>Activates the current menu item or toggles its option.</summary>
    private void ActivateFrontEndSelection()
    {
        switch (state)
        {
            case GameState.Menu:
                switch (menuSelection)
                {
                    case 0: StartPhase(false); break;
                    case 1 when !IsMobile: twoPlayers = !twoPlayers; break;
                    case 2 when !IsMobile: state = GameState.Difficulty; difficultySelection = (int)difficulty; break;
                    case 1 when IsMobile: state = GameState.Difficulty; difficultySelection = (int)difficulty; break;
                    case 3 when !IsMobile: state = GameState.Options; optionsSelection = 0; break;
                    case 2 when IsMobile: state = GameState.Options; optionsSelection = 0; break;
                    case 4 when !IsMobile: state = GameState.Controls; optionsSelection = 0; break;
                    case 3 when IsMobile: Exit(); break;
                    case 5 when !IsMobile: Exit(); break;
                }
                break;
            case GameState.Difficulty:
                difficulty = (DifficultyLevel)difficultySelection;
                StartPhase(false);
                break;
            case GameState.Options:
                if (optionsSelection == 0) hintsEnabled = !hintsEnabled;
                else if (optionsSelection == 1)
                {
                    fullscreenEnabled = !fullscreenEnabled;
                    graphics.IsFullScreen = fullscreenEnabled;
                }
                else state = GameState.Menu;
                break;
            case GameState.Controls:
                if (optionsSelection == 2) state = GameState.Menu;
                else waitingForControl = !waitingForControl;
                break;
        }
    }

    /// <summary>Returns from a child front-end screen to the main menu.</summary>
    private void GoBackFromFrontEnd()
    {
        state = state == GameState.Menu ? GameState.Menu : GameState.Menu;
        waitingForControl = false;
    }

    /// <summary>Handles ghost switching, traps, and mobile gestures during gameplay.</summary>
    private void HandleSwitchInput(KeyboardState keyboard, GamePadState gamePad)
    {
        if (!IsMobile)
        {
            if (keyboard.IsKeyDown(Keys.D1) && previousKeyboard.IsKeyUp(Keys.D1)) SwitchTo(0);
            if (keyboard.IsKeyDown(Keys.D2) && previousKeyboard.IsKeyUp(Keys.D2)) SwitchTo(1);
            if (keyboard.IsKeyDown(Keys.D3) && previousKeyboard.IsKeyUp(Keys.D3)) SwitchTo(2);
            if (keyboard.IsKeyDown(Keys.Tab) && previousKeyboard.IsKeyUp(Keys.Tab)) SwitchToNext();
            if ((keyboard.IsKeyDown(Keys.Space) && previousKeyboard.IsKeyUp(Keys.Space))
                || (keyboard.IsKeyDown(trapKey) && previousKeyboard.IsKeyUp(trapKey))
                || (gamePad.IsButtonDown(Buttons.X) && previousGamePad.IsButtonUp(Buttons.X)))
                TryDropTrap();
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
                if (IsMobile) TryDropTrap();
                continue;
            }
            if (gesture.GestureType != GestureType.Tap) continue;
            var logical = ToLogical(gesture.Position);
            var x = logical.X;
            var y = logical.Y;
            if (y <= OriginY && x >= 12 && x <= 168)
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
                : helpersUnlockedAt >= 0f && elapsed >= helpersUnlockedAt + ReadyDelay + ghost.ReleaseDelay;
            if (!ghost.Released && canLeave)
            {
                ghost.Released = true;
                ghost.DesiredDirection = MazeDirection.Up;
            }
        }
    }

    /// <summary>Places a limited trap fruit at the selected ghost cell.</summary>
    private void TryDropTrap()
    {
        if (!Selected.Alive || !Selected.Released || Selected.TrapFruitsRemaining <= 0)
            return;
        if (traps.Any(trap => trap.Cell == Selected.Cell))
            return;

        traps.Add(new TrapFruit(Selected.Cell));
        Selected.TrapFruitsRemaining--;
    }

    /// <summary>Reads the second desktop gamepad and drives the yellow ghost in local co-op.</summary>
    /// <summary>Reads player two's gamepad and drives the local co-op ghost.</summary>
    private void HandleSecondPlayerInput(GamePadState gamePad)
    {
        if (IsMobile || !twoPlayers || ghosts.Count < 2 || !ghosts[1].Alive) return;
        var direction = ReadGamePadDirection(gamePad);
        if (direction != MazeDirection.None) ghosts[1].DesiredDirection = direction;
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

    /// <summary>Trains the ghost policy from the primary player's latest action.</summary>
    private void LearnFromPlayer()
    {
        if (!Selected.Released || Selected.DesiredDirection == MazeDirection.None) return;
        var stateKey = BuildState(Selected.Cell, NearestPacman(Selected.Cell));
        var next = Selected.Cell + Selected.DesiredDirection.Offset();
        var reward = maze.IsWalkable(next) ? .2f : -.35f;
        ghostPolicy.Learn(stateKey, Selected.DesiredDirection, reward,
            BuildState(next, NearestPacman(next)));
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
                var target = NearestPacman(ghost.Cell);
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
        var target = pacmen.Where(p => p.Alive).OrderBy(p => Manhattan(ghost.Cell, p.Cell)).FirstOrDefault();
        return target == null ? MazeDirection.None : MazePathfinding.AStar(maze, ghost.Cell, target.Cell);
    }

    /// <summary>Updates every Pac-Man clone and alternates its navigation policy.</summary>
    private void UpdatePacmen(float delta)
    {
        foreach (var pacman in pacmen.Where(p => p.Alive).ToList())
        {
            pacman.PowerTime = Math.Max(0f, pacman.PowerTime - delta);
            pacman.TrapTime = Math.Max(0f, pacman.TrapTime - delta);
            if (!pacman.IsMoving)
            {
                var target = ghosts.Where(g => g.Alive).OrderBy(g => Manhattan(pacman.Cell, g.Cell)).FirstOrDefault();
                if (target == null) continue;
                if (pacman.PelletsSinceAlgorithmChange >= AlgorithmSwitchEveryPellets)
                {
                    pacmanAlgorithm = (NavigationAlgorithm)random.Next(3);
                    pacman.PelletsSinceAlgorithmChange = 0;
                }
                var trap = traps.OrderBy(item => Manhattan(pacman.Cell, item.Cell)).FirstOrDefault();
                var targetCell = trap?.Cell ?? target.Cell;
                if (!maze.IsPacmanWalkable(targetCell)) targetCell = maze.ClosestPacmanCell(targetCell);
                pacman.DesiredDirection = MazePathfinding.AStar(maze, pacman.Cell, targetCell, pacmanRoute: true);
                if (trap == null && pacmanAlgorithm == NavigationAlgorithm.TravellerSalesman)
                    pacman.DesiredDirection = MazePathfinding.AStar(maze, pacman.Cell,
                        maze.ClosestPacmanCell(Selected.Cell), pacmanRoute: true);
                var learned = pacmanPolicy.Predict(BuildState(pacman.Cell, targetCell), random, .08f);
                if (CanMoveForActor(pacman, learned) && random.NextDouble() < .18) pacman.DesiredDirection = learned;
            }
            var speed = pacman.TrapTime > 0f ? TrappedPacmanSpeed : PacmanSpeed;
            MoveActor(pacman, delta, speed);
        }
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
        }
        else actor.Position += Vector2.Normalize(offset) * travel;
    }

    /// <summary>Collects pellets, power fruit, and trap fruit for live Pac-Men.</summary>
    private void CollectPacmanItems()
    {
        foreach (var pacman in pacmen.Where(p => p.Alive).ToList())
        {
            if (maze.ConsumePellet(pacman.Cell))
            {
                pacman.PelletsSinceAlgorithmChange++;
                pelletsCollected = pelletCount - maze.RemainingPelletCount();
                if (pelletsCollected >= nextCheckpointPellets && pelletsCollected < pelletCount)
                {
                    checkpointRemainingPellets = maze.RemainingPellets();
                    nextCheckpointPellets = Math.Min(pelletCount, nextCheckpointPellets + releaseThreshold);
                }
            }
            if (maze.ConsumePowerFruit(pacman.Cell))
            {
                powerFruitTime = PowerFruitDuration;
                pacman.PowerTime = PowerFruitDuration;
                if (pacmen.Count == 1)
                {
                    var clone = new Pacman(RetroPalette.Orange, maze.PacmanStart)
                    {
                        Position = CellToWorld(maze.PacmanStart),
                        DesiredDirection = MazeDirection.Left,
                        PowerTime = PowerFruitDuration
                    };
                    pacmen.Add(clone);
                }
            }

            var trap = traps.FirstOrDefault(item => item.Cell == pacman.Cell);
            if (trap != null)
            {
                traps.Remove(trap);
                pacman.TrapTime = TrapDuration;
            }
        }
    }

    /// <summary>Resolves vulnerable captures and non-vulnerable ghost losses.</summary>
    private void ResolveCollisions()
    {
        foreach (var ghost in ghosts.Where(g => g.Alive && g.Released).ToList())
        foreach (var pacman in pacmen.Where(p => p.Alive).ToList())
        {
            if (Vector2.Distance(ghost.Position, pacman.Position) > TileSize * .32f) continue;
            var relative = pacman.Cell - ghost.Cell;
            var canCapture = relative == Point.Zero || ghost.Direction == MazeDirectionExtensions.FromDelta(relative);
            if (canCapture && pacman.IsVulnerable)
            {
                CapturePacmanLife();
                return;
            }
            else if (!pacman.IsVulnerable)
            {
                ghost.Alive = false;
                ghost.Released = false;
                if (ReferenceEquals(ghost, Selected)) TransferControlAfterDeath();
            }
        }
    }

    /// <summary>Consumes one Pac-Man life and restores the last pellet checkpoint.</summary>
    private void CapturePacmanLife()
    {
        pacmenCaptured++;
        pacmanLives--;
        score += maze.RemainingPelletCount() * 10;

        foreach (var oldPacman in pacmen) oldPacman.Alive = false;
        traps.Clear();
        powerFruitTime = 0f;
        maze.RestorePellets(checkpointRemainingPellets);
        pelletsCollected = pelletCount - maze.RemainingPelletCount();
        nextCheckpointPellets = Math.Min(pelletCount, pelletsCollected + releaseThreshold);

        if (pacmanLives <= 0)
        {
            state = GameState.Won;
            return;
        }

        pacmen.Clear();
        var pacman = new Pacman(RetroPalette.Red, maze.PacmanStart)
        {
            Position = CellToWorld(maze.PacmanStart)
        };
        pacmen.Add(pacman);

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

    /// <summary>Finds the closest live Pac-Man cell to an origin cell.</summary>
    private Point NearestPacman(Point origin)
    {
        var result = pacmen.Where(p => p.Alive).OrderBy(p => Manhattan(origin, p.Cell)).FirstOrDefault();
        return result?.Cell ?? maze.PacmanStart;
    }

    /// <summary>Returns Manhattan distance between two grid cells.</summary>
    private static int Manhattan(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    /// <summary>Checks whether a direction leads to a traversable cell.</summary>
    private bool CanMove(Point cell, MazeDirection direction) => direction != MazeDirection.None
        && maze.IsWalkable(cell + direction.Offset());

    /// <summary>Checks movement using the actor-specific ghost-house access rule.</summary>
    private bool CanMoveForActor(Actor actor, MazeDirection direction) => direction != MazeDirection.None
        && (actor is Pacman ? maze.IsPacmanWalkable(actor.Cell + direction.Offset()) :
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
        if (state is GameState.Menu or GameState.Difficulty or GameState.Options or GameState.Controls)
            DrawFrontEnd();
        else
        {
            DrawMaze();
            DrawTraps();
            foreach (var pacman in pacmen.Where(p => p.Alive)) DrawPacman(pacman);
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
            var labels = IsMobile
                ? new[] { "MenuPlay", "MenuDifficulty", "MenuOptions", "MenuQuit" }
                : new[] { "MenuPlay", "MenuTwoPlayers", "MenuDifficulty", "MenuOptions", "MenuControls", "MenuQuit" };
            for (var i = 0; i < labels.Length; i++)
            {
                var label = labels[i] == "MenuTwoPlayers"
                    ? GameText.Format("MenuTwoPlayers", twoPlayers ? GameText.Get("OptionsOn") : GameText.Get("OptionsOff"))
                    : GameText.Get(labels[i]);
                DrawMenuRow(label, 180 + i * 42, i == menuSelection);
            }
            spriteBatch.DrawString(hudFont, GameText.Get(IsMobile ? "ControlsMobile" : "ControlsDesktop"),
                new Vector2(170, 438), RetroPalette.Gray);
            return;
        }

        var title = state switch
        {
            GameState.Difficulty => "DifficultyTitle",
            GameState.Options => "OptionsTitle",
            _ => "ControlsTitle"
        };
        spriteBatch.DrawString(hudFont, GameText.Get(title), new Vector2(302, 122), RetroPalette.White);

        if (state == GameState.Difficulty)
        {
            DrawMenuRow(GameText.Get("DifficultyEasy"), 184, difficultySelection == 0);
            DrawMenuRow(GameText.Get("DifficultyNormal"), 226, difficultySelection == 1);
            DrawMenuRow(GameText.Get("DifficultyHard"), 268, difficultySelection == 2);
            spriteBatch.DrawString(hudFont, GameText.Get("Back"), new Vector2(350, 400), RetroPalette.Gray);
        }
        else if (state == GameState.Options)
        {
            DrawMenuRow(GameText.Format("OptionsHints", hintsEnabled ? GameText.Get("OptionsOn") : GameText.Get("OptionsOff")),
                184, optionsSelection == 0);
            DrawMenuRow(GameText.Format("OptionsFullscreen", fullscreenEnabled ? GameText.Get("OptionsOn") : GameText.Get("OptionsOff")),
                226, optionsSelection == 1);
            DrawMenuRow(GameText.Get("Back"), 268, optionsSelection == 2);
            spriteBatch.DrawString(hudFont, GameText.Get("OptionsLanguage"), new Vector2(208, 380), RetroPalette.Gray);
        }
        else
        {
            spriteBatch.DrawString(hudFont, GameText.Get("ControlsDesktop"), new Vector2(216, 182), RetroPalette.Cyan);
            spriteBatch.DrawString(hudFont, GameText.Get("ControlsMove"), new Vector2(216, 224), RetroPalette.White);
            spriteBatch.DrawString(hudFont, GameText.Get("ControlsSwitch"), new Vector2(216, 266), RetroPalette.White);
            spriteBatch.DrawString(hudFont, GameText.Format("ControlsTrap", trapKey), new Vector2(216, 308), RetroPalette.White);
            spriteBatch.DrawString(hudFont, waitingForControl ? GameText.Get("ControlsPressKey") : GameText.Get("ControlsConfigure"),
                new Vector2(216, 362), waitingForControl ? RetroPalette.Yellow : RetroPalette.Gray);
            spriteBatch.DrawString(hudFont, GameText.Get("Back"), new Vector2(350, 420), RetroPalette.Gray);
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

    /// <summary>Draws a red or orange Pac-Man clone without external images.</summary>
    private void DrawPacman(Pacman pacman)
    {
        var x = (int)pacman.Position.X;
        var y = (int)pacman.Position.Y;
        Rect(x - 8, y - 8, 16, 16, pacman.Color);
        Rect(x - 5, y - 11, 10, 22, pacman.Color);
        Rect(x - 11, y - 5, 22, 10, pacman.Color);
        var mouth = pacman.Direction == MazeDirection.Left ? new Rectangle(x - 10, y - 2, 7, 4) :
            pacman.Direction == MazeDirection.Up ? new Rectangle(x - 2, y - 10, 4, 7) :
            pacman.Direction == MazeDirection.Down ? new Rectangle(x - 2, y + 3, 4, 7) :
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
            GameText.Format("HudStats", pacmanLives, pacmenCaptured, helperStatus),
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
        if (elapsed < ReadyDelay && hintsEnabled) spriteBatch.DrawString(hudFont, GameText.Get("ReadyMessage"),
            new Vector2(300, 250), RetroPalette.Yellow);
        if (state == GameState.GameOver) spriteBatch.DrawString(hudFont,
            GameText.Get("GameOverPellets"), new Vector2(215, 300), RetroPalette.Red);
        if (state == GameState.Won) spriteBatch.DrawString(hudFont,
            GameText.Get("WonCapture"), new Vector2(205, 300), RetroPalette.Green);
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
