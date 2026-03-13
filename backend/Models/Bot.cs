// ─── Models/Bot.cs ───────────────────────────────────────────

namespace BotPanel.Models;

public enum BotStatus
{
    Stopped,
    Running,
    Error,
    Starting,
    Stopping
}

public class Bot
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public BotStatus Status { get; set; } = BotStatus.Stopped;
    public string? ContainerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRun { get; set; }
    public string DirectoryPath { get; set; } = string.Empty;
}

public class CreateBotRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Template { get; set; } = "basic";
}

public class BotFileContent
{
    public string Content { get; set; } = string.Empty;
}

public class BotDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ContainerId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastRun { get; set; }
}
