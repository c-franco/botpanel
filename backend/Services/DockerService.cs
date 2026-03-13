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

    // Default resource limits
    private const string CPU_LIMIT    = "1.0";
    private const string MEMORY_LIMIT = "512m";

    // Two images available:
    //   - DEFAULT_IMAGE: python:3.12-slim for standard bots
    //   - CHROME_IMAGE:  python-chrome:latest for bots that need Selenium/Chrome
    //
    // To use Chrome, place a file named ".use-chrome" in the bot directory.
    // Build the Chrome image once with:
    //   docker build -t python-chrome:latest -f docker-images/Dockerfile.chrome-bot docker-images/
    private const string DEFAULT_IMAGE = "python:3.12-slim";
    private const string CHROME_IMAGE  = "python-chrome:latest";
    private const string CHROME_MARKER = ".use-chrome";

    public DockerService(ILogger<DockerService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Creates and runs a Docker container for the given bot.
    ///
    /// All bots run with network=bridge so pip can install dependencies freely
    /// and bots can make outbound connections (APIs, scraping, etc.).
    ///
    /// The only distinction is whether the bot needs Chrome:
    ///   - .use-chrome marker → python-chrome image, rw filesystem, extra shared memory
    ///   - default            → python:3.12-slim, read-only filesystem
    ///
    /// IMPORTANT: The backend runs inside Docker with /bots mounted from the host.
    /// botDirectory is the path inside the backend container (e.g. /bots/my_bot).
    /// BOTS_HOST_PATH env var must match the host-side path of the bots volume.
    /// </summary>
    public async Task<string> RunBotAsync(string botId, string botDirectory, CancellationToken ct = default)
    {
        var containerName = $"botpanel_{botId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        // Resolve host path for the volume mount
        var botsHostPath = Environment.GetEnvironmentVariable("BOTS_HOST_PATH") ?? "/bots";
        var botName      = Path.GetFileName(botDirectory.TrimEnd('/'));
        var absPath      = Path.Combine(botsHostPath, botName);

        // Check for Chrome marker
        var internalPath = Path.GetFullPath(botDirectory);
        var needsChrome  = File.Exists(Path.Combine(internalPath, CHROME_MARKER));

        string image, volumeMode, extraFlags;

        if (needsChrome)
        {
            image      = CHROME_IMAGE;
            volumeMode = "rw";           // Chrome needs to write temp files
            extraFlags = "--shm-size=512m";
            _logger.LogInformation("Bot {Id} starting with Chrome image", botId);
        }
        else
        {
            image      = DEFAULT_IMAGE;
            volumeMode = "ro";           // Read-only: bot code cannot modify itself
            extraFlags = "--read-only --tmpfs /tmp --security-opt no-new-privileges";
            _logger.LogInformation("Bot {Id} starting with default image", botId);
        }

        // All bots use bridge networking so pip and outbound calls work out of the box
        var args = string.Join(" ",
            "run",
            "--detach",
            $"--name {containerName}",
            $"--cpus={CPU_LIMIT}",
            $"--memory={MEMORY_LIMIT}",
            "--network=bridge",
            "--no-healthcheck",
            extraFlags,
            $"--volume \"{absPath}:/app:{volumeMode}\"",
            "--workdir /app",
            image,
            "sh -c \"pip install -r requirements.txt -q --disable-pip-version-check --root-user-action=ignore && python -u bot.py\""
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
        await RunDockerCommand($"stop --time 5 {containerId}", ct);
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
    /// Streams both stdout and stderr from a running container in real-time.
    /// Feeds into SignalR to push logs to the frontend.
    /// </summary>
    public async IAsyncEnumerable<(string stream, string text)> StreamLogsAsync(
        string containerId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker", $"logs --follow --timestamps {containerId}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker logs");

        var channel = System.Threading.Channels.Channel.CreateUnbounded<(string, string)>();

        _ = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line != null) await channel.Writer.WriteAsync(("stdout", StripTimestamp(line)), ct);
            }
            channel.Writer.TryComplete();
        }, ct);

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
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
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
        // Docker --timestamps format: 2025-01-01T12:00:00.000000000Z <message>
        if (line.Length > 32 && line[10] == 'T')
        {
            var spaceIdx = line.IndexOf(' ');
            return spaceIdx >= 0 ? line[(spaceIdx + 1)..] : line;
        }
        return line;
    }
}
