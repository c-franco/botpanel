// ─── Services/DockerService.cs ───────────────────────────────

using System.Diagnostics;
using BotPanel.Models;

namespace BotPanel.Services;

public interface IDockerService
{
    Task<string> RunBotAsync(string botId, string botDirectory, CancellationToken ct = default);
    Task StopBotAsync(string containerId, CancellationToken ct = default);
    Task<bool> IsContainerRunningAsync(string containerId);
    IAsyncEnumerable<(string stream, string text)> StreamLogsAsync(string containerId, CancellationToken ct);
}

public class DockerService : IDockerService
{
    private readonly ILogger<DockerService> _logger;
    private readonly IConfiguration _config;

    // Resource limits
    private const string CPU_LIMIT = "0.5";    // 50% of one CPU
    private const string MEMORY_LIMIT = "256m"; // 256 MB RAM

    public DockerService(ILogger<DockerService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Creates and runs a Docker container for the given bot.
    /// Each bot gets its own isolated container with resource limits.
    /// </summary>
    public async Task<string> RunBotAsync(string botId, string botDirectory, CancellationToken ct = default)
    {
        var containerName = $"botpanel_{botId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var absPath = Path.GetFullPath(botDirectory);

        // Build docker run command with security constraints
        var args = string.Join(" ",
            "run",
            "--detach",
            $"--name {containerName}",
            $"--cpus={CPU_LIMIT}",
            $"--memory={MEMORY_LIMIT}",
            "--network=none",           // No network by default (override in config if needed)
            "--read-only",              // Read-only filesystem (except /tmp)
            "--tmpfs /tmp",             // Writable tmp
            "--no-healthcheck",
            "--security-opt no-new-privileges",  // Prevent privilege escalation
            $"--volume \"{absPath}:/app:ro\"",   // Mount bot files as read-only
            "--workdir /app",
            "python:3.12-slim",
            "sh -c \"pip install -r requirements.txt -q && python -u bot.py\""
        );

        _logger.LogInformation("Starting container: docker {Args}", args);

        var result = await RunDockerCommand(args, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to start container: {result.Stderr}");

        var containerId = result.Stdout.Trim();
        _logger.LogInformation("Container started: {ContainerId}", containerId[..12]);
        return containerId;
    }

    /// <summary>
    /// Stops and removes a running container.
    /// </summary>
    public async Task StopBotAsync(string containerId, CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping container: {ContainerId}", containerId[..12]);

        // Stop (with 5s timeout)
        await RunDockerCommand($"stop --time 5 {containerId}", ct);

        // Remove
        await RunDockerCommand($"rm -f {containerId}", ct);

        _logger.LogInformation("Container removed: {ContainerId}", containerId[..12]);
    }

    /// <summary>
    /// Checks if a container is still running.
    /// </summary>
    public async Task<bool> IsContainerRunningAsync(string containerId)
    {
        var result = await RunDockerCommand($"inspect --format={{{{.State.Running}}}} {containerId}");
        return result.Stdout.Trim() == "true";
    }

    /// <summary>
    /// Streams both stdout and stderr from a running container.
    /// This feeds into SignalR to push logs to the frontend in real-time.
    /// </summary>
    public async IAsyncEnumerable<(string stream, string text)> StreamLogsAsync(
        string containerId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker", $"logs --follow --timestamps {containerId}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker logs");

        var channel = System.Threading.Channels.Channel.CreateUnbounded<(string, string)>();

        // Read stdout
        _ = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line != null) await channel.Writer.WriteAsync(("stdout", StripTimestamp(line)), ct);
            }
            channel.Writer.TryComplete();
        }, ct);

        // Read stderr
        _ = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(ct);
                if (line != null) await channel.Writer.WriteAsync(("stderr", StripTimestamp(line)), ct);
            }
        }, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
            yield return item;

        await process.WaitForExitAsync(ct);
    }

    // ─── Helpers ─────────────────────────────────────────────

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCommand(
        string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }

    private static string StripTimestamp(string line)
    {
        // Docker --timestamps produces: 2025-01-01T12:00:00.000000000Z <message>
        if (line.Length > 32 && line[10] == 'T')
        {
            var spaceIdx = line.IndexOf(' ');
            return spaceIdx >= 0 ? line[(spaceIdx + 1)..] : line;
        }
        return line;
    }
}
