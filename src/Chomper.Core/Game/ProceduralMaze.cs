using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Chomper.Core.Game;

internal enum NavigationAlgorithm
{
    AStar,
    TravellerSalesman,
    FiniteStateMachine
}

internal enum MazeDirection
{
    None,
    Up,
    Right,
    Down,
    Left
}

internal static class MazeDirectionExtensions
{
    /// <summary>Returns the one-cell offset represented by a direction.</summary>
    public static Point Offset(this MazeDirection direction) => direction switch
    {
        MazeDirection.Up => new Point(0, -1),
        MazeDirection.Right => new Point(1, 0),
        MazeDirection.Down => new Point(0, 1),
        MazeDirection.Left => new Point(-1, 0),
        _ => Point.Zero
    };

    /// <summary>Converts a cell delta into a cardinal maze direction.</summary>
    public static MazeDirection FromDelta(Point delta)
    {
        if (delta.X > 0) return MazeDirection.Right;
        if (delta.X < 0) return MazeDirection.Left;
        if (delta.Y > 0) return MazeDirection.Down;
        if (delta.Y < 0) return MazeDirection.Up;
        return MazeDirection.None;
    }
}

internal sealed class ProceduralMaze
{
    public const int Width = 31;
    public const int Height = 19;

    private readonly bool[,] walkable = new bool[Width, Height];
    private readonly bool[,] pellets = new bool[Width, Height];
    private readonly bool[,] powerFruit = new bool[Width, Height];
    private readonly HashSet<Point> ghostHouse = new();

    public Point GhostHouseCenter { get; }
    public Point PacmanStart { get; private set; }
    public IReadOnlyList<Point> GhostStarts { get; private set; }

    /// <summary>Creates an empty maze whose cells are populated by Generate.</summary>
    private ProceduralMaze(Point houseCenter)
    {
        GhostHouseCenter = houseCenter;
        GhostStarts = Array.Empty<Point>();
    }

    /// <summary>Generates a deterministic maze for the requested phase and seed.</summary>
    public static ProceduralMaze Generate(int phase, int seed = 739391)
    {
        var center = new Point(Width / 2, Height / 2);
        var maze = new ProceduralMaze(center);
        var random = new Random(unchecked(seed + phase * 104729));
        var stack = new Stack<Point>();
        var start = new Point(1, 1);
        maze.walkable[start.X, start.Y] = true;
        stack.Push(start);
        var carving = new[] { new Point(2, 0), new Point(-2, 0), new Point(0, 2), new Point(0, -2) };

        while (stack.Count > 0)
        {
            var current = stack.Peek();
            var choices = new List<Point>();
            foreach (var delta in carving)
            {
                var next = current + delta;
                if (next.X > 0 && next.X < Width - 1 && next.Y > 0 && next.Y < Height - 1
                    && !maze.walkable[next.X, next.Y])
                    choices.Add(next);
            }

            if (choices.Count == 0)
            {
                stack.Pop();
                continue;
            }

            var nextCell = choices[random.Next(choices.Count)];
            var between = new Point((current.X + nextCell.X) / 2, (current.Y + nextCell.Y) / 2);
            maze.walkable[between.X, between.Y] = true;
            maze.walkable[nextCell.X, nextCell.Y] = true;
            stack.Push(nextCell);
        }

        for (var i = 0; i < 18; i++)
        {
            var x = 1 + random.Next((Width - 2) / 2) * 2;
            var y = 1 + random.Next((Height - 2) / 2) * 2;
            if (random.Next(2) == 0 && x < Width - 2) maze.walkable[x + 1, y] = true;
            else if (y < Height - 2) maze.walkable[x, y + 1] = true;
        }

        for (var y = center.Y - 1; y <= center.Y + 1; y++)
        for (var x = center.X - 2; x <= center.X + 2; x++)
        {
            maze.walkable[x, y] = true;
            maze.ghostHouse.Add(new Point(x, y));
        }

        // Keep a walkable one-cell ring around the house so Pac-Man can
        // route around its perimeter without being forced into the house.
        for (var y = center.Y - 2; y <= center.Y + 2; y++)
        for (var x = center.X - 3; x <= center.X + 3; x++)
            maze.walkable[x, y] = true;

        maze.walkable[center.X, center.Y - 2] = true;
        maze.walkable[center.X, center.Y - 3] = true;
        maze.walkable[center.X, center.Y - 4] = true;

        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            maze.pellets[x, y] = maze.walkable[x, y] && !maze.ghostHouse.Contains(new Point(x, y));

        var powerPositions = new[]
        {
            new Point(1, 1), new Point(Width - 2, 1),
            new Point(1, Height - 2), new Point(Width - 2, Height - 2)
        };
        foreach (var power in powerPositions)
        {
            maze.powerFruit[power.X, power.Y] = maze.walkable[power.X, power.Y];
            maze.pellets[power.X, power.Y] = false;
        }

        maze.GhostStarts = new[]
        {
            new Point(center.X, center.Y),
            new Point(center.X - 1, center.Y),
            new Point(center.X + 1, center.Y)
        };
        maze.PacmanStart = maze.WalkableCells()
            .Where(cell => !maze.ghostHouse.Contains(cell))
            .OrderByDescending(cell => Math.Abs(cell.X - center.X) + Math.Abs(cell.Y - center.Y))
            .ThenBy(cell => cell.Y)
            .ThenBy(cell => cell.X)
            .First();
        maze.pellets[maze.PacmanStart.X, maze.PacmanStart.Y] = false;
        return maze;
    }

    /// <summary>Checks whether a cell can be traversed by an actor.</summary>
    public bool IsWalkable(Point cell) => cell.X >= 0 && cell.X < Width && cell.Y >= 0 && cell.Y < Height
        && walkable[cell.X, cell.Y];

    /// <summary>Checks whether Pac-Man may enter a traversable cell.</summary>
    public bool IsPacmanWalkable(Point cell) => IsWalkable(cell) && !ghostHouse.Contains(cell);

    /// <summary>Checks whether a regular pellet remains at a cell.</summary>
    public bool HasPellet(Point cell) => pellets[cell.X, cell.Y];
    /// <summary>Checks whether a power fruit remains at a cell.</summary>
    public bool HasPowerFruit(Point cell) => powerFruit[cell.X, cell.Y];

    /// <summary>Counts regular pellets still present in this maze.</summary>
    public int RemainingPelletCount()
    {
        var count = 0;
        foreach (var cell in WalkableCells())
            if (HasPellet(cell)) count++;
        return count;
    }

    /// <summary>Copies remaining pellet positions for checkpoint restoration.</summary>
    public HashSet<Point> RemainingPellets()
    {
        var result = new HashSet<Point>();
        foreach (var cell in WalkableCells())
            if (HasPellet(cell)) result.Add(cell);
        return result;
    }

    /// <summary>Restores pellets to a previously captured checkpoint.</summary>
    public void RestorePellets(ISet<Point> checkpoint)
    {
        foreach (var cell in WalkableCells())
            pellets[cell.X, cell.Y] = checkpoint.Contains(cell);
    }

    /// <summary>Consumes one regular pellet and reports whether one was present.</summary>
    public bool ConsumePellet(Point cell)
    {
        if (!pellets[cell.X, cell.Y]) return false;
        pellets[cell.X, cell.Y] = false;
        return true;
    }

    /// <summary>Consumes one power fruit and reports whether one was present.</summary>
    public bool ConsumePowerFruit(Point cell)
    {
        if (!powerFruit[cell.X, cell.Y]) return false;
        powerFruit[cell.X, cell.Y] = false;
        return true;
    }

    /// <summary>Enumerates every traversable cell in stable row-major order.</summary>
    public IEnumerable<Point> WalkableCells()
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if (walkable[x, y]) yield return new Point(x, y);
    }

    /// <summary>Enumerates traversable cardinal neighbours of a cell.</summary>
    public IEnumerable<Point> Neighbours(Point cell)
    {
        var directions = new[] { new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0) };
        foreach (var delta in directions)
        {
            var next = cell + delta;
            if (IsWalkable(next)) yield return next;
        }
    }

    /// <summary>Enumerates neighbours while optionally excluding the ghost house.</summary>
    public IEnumerable<Point> PacmanNeighbours(Point cell)
    {
        foreach (var next in Neighbours(cell))
            if (IsPacmanWalkable(next)) yield return next;
    }

    /// <summary>Returns the closest cell that Pac-Man may use as a chase target.</summary>
    public Point ClosestPacmanCell(Point origin) => WalkableCells()
        .Where(IsPacmanWalkable)
        .OrderBy(cell => Math.Abs(cell.X - origin.X) + Math.Abs(cell.Y - origin.Y))
        .ThenBy(cell => cell.Y)
        .ThenBy(cell => cell.X)
        .First();
}

internal static class MazePathfinding
{
    /// <summary>Returns the first A* step from an origin toward a target.</summary>
    public static MazeDirection AStar(ProceduralMaze maze, Point origin, Point target, bool pacmanRoute = false)
    {
        if (origin == target) return MazeDirection.None;
        var frontier = new PriorityQueue<Point, int>();
        var cameFrom = new Dictionary<Point, Point>();
        var cost = new Dictionary<Point, int> { [origin] = 0 };
        frontier.Enqueue(origin, 0);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (current == target) break;
            var neighbours = pacmanRoute ? maze.PacmanNeighbours(current) : maze.Neighbours(current);
            foreach (var next in neighbours)
            {
                var nextCost = cost[current] + 1;
                if (!cost.TryGetValue(next, out var oldCost) || nextCost < oldCost)
                {
                    cost[next] = nextCost;
                    cameFrom[next] = current;
                    frontier.Enqueue(next, nextCost + Math.Abs(next.X - target.X) + Math.Abs(next.Y - target.Y));
                }
            }
        }

        if (!cameFrom.ContainsKey(target)) return MazeDirection.None;
        var step = target;
        while (cameFrom.TryGetValue(step, out var previous) && previous != origin) step = previous;
        return MazeDirectionExtensions.FromDelta(step - origin);
    }

    /// <summary>Returns the neighbouring step that maximizes distance from a threat.</summary>
    public static MazeDirection Flee(ProceduralMaze maze, Point origin, Point threat)
    {
        var result = MazeDirection.None;
        var best = -1;
        foreach (var next in maze.Neighbours(origin))
        {
            var distance = Math.Abs(next.X - threat.X) + Math.Abs(next.Y - threat.Y);
            if (distance > best)
            {
                best = distance;
                result = MazeDirectionExtensions.FromDelta(next - origin);
            }
        }
        return result;
    }
}
