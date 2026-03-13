// ─── Services/LogStreamingService.cs ─────────────────────────

using BotPanel.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BotPanel.Services;

public interface ILogStreamingService
{
    Task StartStreamingAsync(string botId, string containerId, CancellationToken ct);
    void StopStreaming(string botId);
}

public class LogStreamingService : ILogStreamingService
{
    private readonly IDockerService _docker;
    private readonly IHubContext<LogHub> _hub;
    private readonly ILogger<LogStreamingService> _logger;
    private readonly Dictionary<string, CancellationTokenSource> _streamTokens = new();

    public LogStreamingService(
        IDockerService docker,
        IHubContext<LogHub> hub,
        ILogger<LogStreamingService> logger)
    {
        _docker = docker;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Starts streaming Docker container logs to all connected SignalR clients.
    /// Runs in background — each log line is pushed to the "ReceiveLog" event.
    /// </summary>
    public async Task StartStreamingAsync(string botId, string containerId, CancellationToken externalCt)
    {
        StopStreaming(botId); // Stop any existing stream

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _streamTokens[botId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting log stream for bot {BotId}", botId);
                await foreach (var (stream, text) in _docker.StreamLogsAsync(containerId, cts.Token))
                {
                    // Push to all clients watching this bot
                    await _hub.Clients.Group(botId)
                        .SendAsync("ReceiveLog", botId, stream, text, cts.Token);
                }

                // Container finished
                await _hub.Clients.Group(botId)
                    .SendAsync("BotStatusChanged", botId, "stopped", cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Log stream cancelled for bot {BotId}", botId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Log stream error for bot {BotId}", botId);
                await _hub.Clients.Group(botId)
                    .SendAsync("BotStatusChanged", botId, "error");
            }
            finally
            {
                _streamTokens.Remove(botId);
            }
        }, cts.Token);
    }

    public void StopStreaming(string botId)
    {
        if (_streamTokens.TryGetValue(botId, out var cts))
        {
            cts.Cancel();
            _streamTokens.Remove(botId);
        }
    }
}
