using System;

namespace Chomper.Core.Multiplayer;

/// <summary>Local Nakama connection settings read from environment variables at startup.</summary>
internal sealed record NakamaConnectionOptions(string Host, int Port, string ServerKey)
{
    /// <summary>Creates development defaults matching the Docker Compose service.</summary>
    public static NakamaConnectionOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("CHOMPER_NAKAMA_HOST") ?? "127.0.0.1",
        int.TryParse(Environment.GetEnvironmentVariable("CHOMPER_NAKAMA_PORT"), out var port) ? port : 7350,
        Environment.GetEnvironmentVariable("CHOMPER_NAKAMA_SERVER_KEY") ?? "defaultkey");

    /// <summary>Builds the HTTP endpoint consumed by a future Nakama client adapter.</summary>
    public string HttpEndpoint => $"http://{Host}:{Port}";
}
