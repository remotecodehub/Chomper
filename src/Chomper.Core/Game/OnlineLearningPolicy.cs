using System;
using System.Collections.Generic;

namespace Chomper.Core.Game;

/// <summary>
/// Dependency-free online Q-learning table used to imitate the controlled ghost.
/// </summary>
internal sealed class OnlineLearningPolicy
{
    private readonly Dictionary<string, float[]> values = new();
    private const float LearningRate = 0.12f;
    private const float Discount = 0.86f;

    public void Learn(string state, MazeDirection action, float reward, string nextState)
    {
        var current = Get(state);
        var next = Get(nextState);
        var bestNext = 0f;
        foreach (var value in next) bestNext = Math.Max(bestNext, value);
        current[(int)action] += LearningRate * (reward + Discount * bestNext - current[(int)action]);
    }

    public MazeDirection Predict(string state, Random random, float exploration = 0.08f)
    {
        if (random.NextDouble() < exploration) return (MazeDirection)(1 + random.Next(4));
        var scores = Get(state);
        var best = MazeDirection.Up;
        var bestScore = float.MinValue;
        for (var i = 1; i <= 4; i++)
        {
            if (scores[i] > bestScore)
            {
                bestScore = scores[i];
                best = (MazeDirection)i;
            }
        }
        return best;
    }

    private float[] Get(string state)
    {
        if (!values.TryGetValue(state, out var result))
        {
            result = new float[5];
            values[state] = result;
        }
        return result;
    }
}
