// ─── Hubs/LogHub.cs ──────────────────────────────────────────

using Microsoft.AspNetCore.SignalR;
using BotPanel.Resources;

namespace BotPanel.Hubs;

/// <summary>
/// SignalR hub for real-time log streaming.
/// Clients subscribe to a specific bot's group to receive its logs.
/// 
/// Client-side usage (JavaScript):
///   connection.invoke("SubscribeToBot", "my_bot_id");
///   connection.on("ReceiveLog", (botId, stream, text) => { ... });
///   connection.on("BotStatusChanged", (botId, status) => { ... });
/// </summary>
public class LogHub : Hub
{
    private readonly ILogger<LogHub> _logger;

    public LogHub(ILogger<LogHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Client calls this to start receiving logs for a specific bot.
    /// </summary>
    public async Task SubscribeToBot(string botId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, botId);
        _logger.LogDebug(AppResources.SubscribedClient,
            Context.ConnectionId, botId);
    }

    /// <summary>
    /// Client calls this to stop receiving logs for a specific bot.
    /// </summary>
    public async Task UnsubscribeFromBot(string botId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, botId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug(AppResources.DisconnectedClient, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
