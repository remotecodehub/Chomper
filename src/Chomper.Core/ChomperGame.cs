using System;
using System.Collections.Generic;
using System.Linq;
using Chomper.Core.Game;
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
    private const int TileSize = 24;
    private const float ReadyDelay = 0.65f;
    private const float PowerFruitDuration = 8f;
    private const float GhostSpeed = 64f;
    private const float PacmanSpeed = GhostSpeed * 1.75f;
    private const float TrappedPacmanSpeed = GhostSpeed * 0.5f;
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

    private int OriginX => (graphics.PreferredBackBufferWidth - ProceduralMaze.Width * TileSize) / 2;
    private const int OriginY = 72;
    private Ghost Selected => ghosts[selectedGhost];

    private enum GameState
    {
        Playing,
        GameOver,
        Won
    }

    private abstract class Actor
    {
        public Point Cell;
        public Point TargetCell;
        public Vector2 Position;
        public MazeDirection Direction = MazeDirection.Up;
        public MazeDirection DesiredDirection = MazeDirection.Up;
        public bool IsMoving;
        public bool Alive = true;

        protected Actor(Point cell) => Cell = cell;
    }

    private sealed class Ghost : Actor
    {
        public string Name { get; }
        public Color Color { get; }
        public NavigationAlgorithm Algorithm { get; }
        public float ReleaseDelay { get; }
        public bool Released;
        public int TrapFruitsRemaining;

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

        public bool IsVulnerable => PowerTime > 0f || TrapTime > 0f;

        public Pacman(Color color, Point cell) : base(cell) => Color = color;
    }

    private sealed class TrapFruit
    {
        public Point Cell { get; }

        public TrapFruit(Point cell) => Cell = cell;
    }

    public ChomperGame()
    {
        graphics = new GraphicsDeviceManager(this);
        graphics.PreferredBackBufferWidth = 800;
        graphics.PreferredBackBufferHeight = 560;
        graphics.SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void Initialize()
    {
        TouchPanel.EnabledGestures = GestureType.Flick | GestureType.Tap;
        base.Initialize();
    }

    protected override void LoadContent()
    {
        spriteBatch = new SpriteBatch(GraphicsDevice);
        hudFont = Content.Load<SpriteFont>("Fonts/Hud");
        pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData(new[] { Color.White });
        StartPhase(false);
    }

    private void StartPhase(bool nextPhase)
    {
        if (nextPhase) phase = (phase + 1) % 7;
        maze = ProceduralMaze.Generate(phase);
        ghosts.Clear();
        pacmen.Clear();
        traps.Clear();
        ghosts.Add(new Ghost("VERDE", new Color(56, 220, 106), NavigationAlgorithm.AStar,
            maze.GhostStarts[0], 0f));
        ghosts.Add(new Ghost("AMARELO", new Color(246, 218, 64), NavigationAlgorithm.TravellerSalesman,
            maze.GhostStarts[1], 2.5f));
        ghosts.Add(new Ghost("AZUL", new Color(68, 155, 255), NavigationAlgorithm.FiniteStateMachine,
            maze.GhostStarts[2], 4.5f));
        foreach (var ghost in ghosts) ghost.Position = CellToWorld(ghost.Cell);

        pacmen.Add(new Pacman(new Color(230, 42, 44), maze.PacmanStart));
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
        state = GameState.Playing;
        previousPlayerCell = Selected.Cell;
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        if (keyboard.IsKeyDown(Keys.Escape) || GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed)
        {
            Exit();
            return;
        }

        if (state != GameState.Playing)
        {
            if (keyboard.IsKeyDown(Keys.Enter) && previousKeyboard.IsKeyUp(Keys.Enter))
                StartPhase(state == GameState.Won);
            previousKeyboard = keyboard;
            Window.Title = state == GameState.Won ? "Chomper | PAC-MAN CAPTURADO 3X | Enter" :
                "Chomper | PAC-MAN COMEU TODAS AS PEBBLES | Enter";
            base.Update(gameTime);
            return;
        }

        var delta = Math.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 0.05f);
        elapsed += delta;
        powerFruitTime = Math.Max(0f, powerFruitTime - delta);
        var gamePad = GamePad.GetState(PlayerIndex.One);
        HandleSwitchInput(keyboard, gamePad);
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
        Window.Title = $"Chomper | FASE {phase + 1} | SCORE {score} | VIDAS PAC-MAN {pacmanLives}";
        base.Update(gameTime);
    }

    private void HandleSwitchInput(KeyboardState keyboard, GamePadState gamePad)
    {
        if (keyboard.IsKeyDown(Keys.D1) && previousKeyboard.IsKeyUp(Keys.D1)) SwitchTo(0);
        if (keyboard.IsKeyDown(Keys.D2) && previousKeyboard.IsKeyUp(Keys.D2)) SwitchTo(1);
        if (keyboard.IsKeyDown(Keys.D3) && previousKeyboard.IsKeyUp(Keys.D3)) SwitchTo(2);
        if (keyboard.IsKeyDown(Keys.Tab) && previousKeyboard.IsKeyUp(Keys.Tab)) SwitchToNext();
        if ((keyboard.IsKeyDown(Keys.Space) && previousKeyboard.IsKeyUp(Keys.Space))
            || (keyboard.IsKeyDown(Keys.F) && previousKeyboard.IsKeyUp(Keys.F))
            || (gamePad.IsButtonDown(Buttons.X) && previousGamePad.IsButtonUp(Buttons.X)))
            TryDropTrap();

        while (TouchPanel.IsGestureAvailable)
        {
            var gesture = TouchPanel.ReadGesture();
            if (gesture.GestureType == GestureType.Flick && gesture.Delta.LengthSquared() >= 25f)
            {
                swipeDirection = FromVector(gesture.Delta);
                continue;
            }
            if (gesture.GestureType != GestureType.Tap) continue;
            var x = gesture.Position.X;
            var y = gesture.Position.Y;
            if (y <= OriginY && x >= 12 && x <= 168)
                SwitchTo(MathHelper.Clamp((int)((x - 12) / 52), 0, 2));
        }
    }

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

    private void SwitchTo(int index)
    {
        if (index < 0 || index >= ghosts.Count || !ghosts[index].Alive) return;
        selectedGhost = index;
        ghosts[index].Released = true;
        ghosts[index].DesiredDirection = MazeDirection.Up;
        previousPlayerCell = ghosts[index].Cell;
    }

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

    private void TryDropTrap()
    {
        if (!Selected.Alive || !Selected.Released || Selected.TrapFruitsRemaining <= 0)
            return;
        if (traps.Any(trap => trap.Cell == Selected.Cell))
            return;

        traps.Add(new TrapFruit(Selected.Cell));
        Selected.TrapFruitsRemaining--;
    }

    private MazeDirection ReadPlayerDirection()
    {
        if (swipeDirection != MazeDirection.None)
        {
            var direction = swipeDirection;
            swipeDirection = MazeDirection.None;
            return direction;
        }

        if (Keyboard.GetState().IsKeyDown(Keys.W) || Keyboard.GetState().IsKeyDown(Keys.Up)) return MazeDirection.Up;
        if (Keyboard.GetState().IsKeyDown(Keys.D) || Keyboard.GetState().IsKeyDown(Keys.Right)) return MazeDirection.Right;
        if (Keyboard.GetState().IsKeyDown(Keys.S) || Keyboard.GetState().IsKeyDown(Keys.Down)) return MazeDirection.Down;
        if (Keyboard.GetState().IsKeyDown(Keys.A) || Keyboard.GetState().IsKeyDown(Keys.Left)) return MazeDirection.Left;

        var pad = GamePad.GetState(PlayerIndex.One);
        if (pad.DPad.Up == ButtonState.Pressed || pad.ThumbSticks.Left.Y > .35f) return MazeDirection.Up;
        if (pad.DPad.Right == ButtonState.Pressed || pad.ThumbSticks.Left.X > .35f) return MazeDirection.Right;
        if (pad.DPad.Down == ButtonState.Pressed || pad.ThumbSticks.Left.Y < -.35f) return MazeDirection.Down;
        if (pad.DPad.Left == ButtonState.Pressed || pad.ThumbSticks.Left.X < -.35f) return MazeDirection.Left;
        return MazeDirection.None;
    }

    private static MazeDirection FromVector(Vector2 delta) => Math.Abs(delta.X) > Math.Abs(delta.Y)
        ? (delta.X >= 0 ? MazeDirection.Right : MazeDirection.Left)
        : (delta.Y >= 0 ? MazeDirection.Down : MazeDirection.Up);

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

    private void UpdateGhosts(float delta)
    {
        foreach (var ghost in ghosts)
        {
            if (!ghost.Alive || !ghost.Released) continue;
            if (ghost != Selected && !ghost.IsMoving)
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

    private MazeDirection TspStep(Ghost ghost)
    {
        var target = pacmen.Where(p => p.Alive).OrderBy(p => Manhattan(ghost.Cell, p.Cell)).FirstOrDefault();
        return target == null ? MazeDirection.None : MazePathfinding.AStar(maze, ghost.Cell, target.Cell);
    }

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
                pacman.DesiredDirection = MazePathfinding.AStar(maze, pacman.Cell, targetCell);
                if (trap == null && pacmanAlgorithm == NavigationAlgorithm.TravellerSalesman)
                    pacman.DesiredDirection = MazePathfinding.AStar(maze, pacman.Cell, Selected.Cell);
                var learned = pacmanPolicy.Predict(BuildState(pacman.Cell, targetCell), random, .08f);
                if (CanMove(pacman.Cell, learned) && random.NextDouble() < .18) pacman.DesiredDirection = learned;
            }
            var speed = pacman.TrapTime > 0f ? TrappedPacmanSpeed : PacmanSpeed;
            MoveActor(pacman, delta, speed);
        }
    }

    private void MoveActor(Actor actor, float delta, float speed)
    {
        if (!actor.IsMoving)
        {
            if (CanMove(actor.Cell, actor.DesiredDirection)) actor.Direction = actor.DesiredDirection;
            else if (!CanMove(actor.Cell, actor.Direction)) actor.Direction = MazeDirection.None;
            if (CanMove(actor.Cell, actor.Direction))
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
                    var clone = new Pacman(new Color(247, 143, 45), maze.PacmanStart)
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
        var pacman = new Pacman(new Color(230, 42, 44), maze.PacmanStart)
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

    private Point NearestPacman(Point origin)
    {
        var result = pacmen.Where(p => p.Alive).OrderBy(p => Manhattan(origin, p.Cell)).FirstOrDefault();
        return result?.Cell ?? maze.PacmanStart;
    }

    private static int Manhattan(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    private bool CanMove(Point cell, MazeDirection direction) => direction != MazeDirection.None
        && maze.IsWalkable(cell + direction.Offset());

    private string BuildState(Point cell, Point target)
    {
        var walls = string.Concat(
            CanMove(cell, MazeDirection.Up) ? '0' : '1',
            CanMove(cell, MazeDirection.Right) ? '0' : '1',
            CanMove(cell, MazeDirection.Down) ? '0' : '1',
            CanMove(cell, MazeDirection.Left) ? '0' : '1');
        return $"{cell.X},{cell.Y}|{target.X},{target.Y}|{walls}";
    }

    private Vector2 CellToWorld(Point cell) => new(OriginX + cell.X * TileSize + TileSize / 2f,
        OriginY + cell.Y * TileSize + TileSize / 2f);

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(4, 7, 22));
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        DrawMaze();
        DrawTraps();
        foreach (var pacman in pacmen.Where(p => p.Alive)) DrawPacman(pacman);
        foreach (var ghost in ghosts.Where(g => g.Alive)) DrawGhost(ghost, ghost == Selected);
        DrawHud();
        spriteBatch.End();
        base.Draw(gameTime);
    }

    private void DrawMaze()
    {
        for (var y = 0; y < ProceduralMaze.Height; y++)
        for (var x = 0; x < ProceduralMaze.Width; x++)
        {
            var cell = new Point(x, y);
            var rect = new Rectangle(OriginX + x * TileSize, OriginY + y * TileSize, TileSize, TileSize);
            if (!maze.IsWalkable(cell))
            {
                Rect(rect.X, rect.Y, rect.Width, rect.Height, new Color(14, 30, 72));
                Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, 2, new Color(36, 87, 150));
                Rect(rect.X + 2, rect.Y + 2, 2, rect.Height - 4, new Color(36, 87, 150));
            }
            else if (maze.HasPellet(cell))
                Rect(rect.Center.X - 2, rect.Center.Y - 2, 4, 4, new Color(255, 222, 132));
            if (maze.HasPowerFruit(cell))
            {
                Rect(rect.Center.X - 5, rect.Center.Y - 4, 10, 8, new Color(252, 72, 111));
                Rect(rect.Center.X - 2, rect.Center.Y - 7, 4, 3, new Color(62, 218, 97));
            }
        }

        var house = new Rectangle(OriginX + (maze.GhostHouseCenter.X - 2) * TileSize,
            OriginY + (maze.GhostHouseCenter.Y - 1) * TileSize, TileSize * 5, TileSize * 3);
        Rect(house.X, house.Y, house.Width, 2, new Color(154, 71, 201));
        Rect(house.X, house.Bottom - 2, house.Width, 2, new Color(154, 71, 201));
        Rect(house.X, house.Y, 2, house.Height, new Color(154, 71, 201));
        Rect(house.Right - 2, house.Y, 2, house.Height, new Color(154, 71, 201));
    }

    private void DrawGhost(Ghost ghost, bool selected)
    {
        var x = (int)ghost.Position.X;
        var y = (int)ghost.Position.Y;
        var color = powerFruitTime > 0 ? new Color(80, 110, 190) : ghost.Color;
        Rect(x - 9, y - 8, 18, 17, color);
        Rect(x - 7, y - 11, 14, 5, color);
        Rect(x - 9, y + 7, 5, 4, color);
        Rect(x - 2, y + 7, 5, 4, color);
        Rect(x + 5, y + 7, 4, 4, color);
        Rect(x - 6, y - 5, 5, 6, Color.White);
        Rect(x + 2, y - 5, 5, 6, Color.White);
        Rect(x - 4, y - 3, 2, 3, Color.Black);
        Rect(x + 4, y - 3, 2, 3, Color.Black);
        if (selected) Rect(x - 12, y - 16, 24, 2, Color.White);
    }

    private void DrawTraps()
    {
        foreach (var trap in traps)
        {
            var center = CellToWorld(trap.Cell);
            Rect((int)center.X - 6, (int)center.Y - 5, 12, 10, new Color(255, 122, 38));
            Rect((int)center.X - 3, (int)center.Y - 8, 6, 3, new Color(238, 213, 67));
            Rect((int)center.X - 2, (int)center.Y - 2, 4, 4, Color.Black);
        }
    }

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
        spriteBatch.Draw(pixel, mouth, new Color(4, 7, 22));
    }

    private void DrawHud()
    {
        Rect(0, 0, graphics.PreferredBackBufferWidth, OriginY - 2, new Color(3, 5, 18));
        spriteBatch.DrawString(hudFont,
            $"FASE {phase + 1}   PEBBLES: {maze.RemainingPelletCount()}   SCORE: {score:00000}",
            new Vector2(10, 6), Color.White);

        var helperStatus = helpersUnlockedAt < 0f
            ? $"HELPERS: AGUARDAM {releaseThreshold} PEBBLES"
            : "HELPERS: LIBERADOS";
        spriteBatch.DrawString(hudFont,
            $"PACMAN VIDAS: {pacmanLives}   CAPTURAS: {pacmenCaptured}/3   {helperStatus}",
            new Vector2(10, 28), Color.LightGray);

        for (var i = 0; i < ghosts.Count; i++)
        {
            var ghost = ghosts[i];
            var color = ghost.Alive ? ghost.Color : new Color(80, 80, 90);
            var x = 10 + i * 58;
            Rect(x, 52, 14, 10, color);
            spriteBatch.DrawString(hudFont, (i + 1).ToString(), new Vector2(x + 18, 47),
                i == selectedGhost ? Color.White : color);
        }

        var status = powerFruitTime > 0 ? $"FRUTA: {powerFruitTime:0.0}s  PACMAN DUPLO" :
            "1/2/3: TROCAR   SPACE/F: ARMADILHA   MOBILE: SWIPE";
        spriteBatch.DrawString(hudFont, status, new Vector2(190, 48),
            powerFruitTime > 0 ? Color.Pink : Color.LightGray);
        spriteBatch.DrawString(hudFont, $"ARMADILHAS: {Selected.TrapFruitsRemaining}",
            new Vector2(630, 28), Color.Orange);
        if (elapsed < ReadyDelay) spriteBatch.DrawString(hudFont, "SAIA DO CONTAINER!",
            new Vector2(300, 250), Color.Yellow);
        if (state == GameState.GameOver) spriteBatch.DrawString(hudFont,
            "PAC-MAN COMEU TODAS AS PEBBLES - GAME OVER", new Vector2(215, 300), Color.Red);
        if (state == GameState.Won) spriteBatch.DrawString(hudFont,
            "PAC-MAN CAPTURADO 3X! ENTER PARA A PROXIMA FASE", new Vector2(205, 300), Color.LimeGreen);
    }

    private void Rect(int x, int y, int width, int height, Color color) =>
        spriteBatch.Draw(pixel, new Rectangle(x, y, width, height), color);

    protected override void UnloadContent()
    {
        pixel?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }
}
