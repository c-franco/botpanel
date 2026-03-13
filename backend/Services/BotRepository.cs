// ─── Services/BotRepository.cs ───────────────────────────────

using System.Collections.Concurrent;
using BotPanel.Models;

namespace BotPanel.Services;

public interface IBotRepository
{
    IEnumerable<Bot> GetAll();
    Bot? GetById(string id);
    Bot Create(CreateBotRequest request);
    void Delete(string id);
    void UpdateStatus(string id, BotStatus status, string? containerId = null);
    string GetFilePath(string botId, string filename);
    string GetBotDirectory(string botId);
}

public class BotRepository : IBotRepository
{
    private readonly ConcurrentDictionary<string, Bot> _bots = new();
    private readonly string _botsBasePath;
    private readonly ILogger<BotRepository> _logger;

    private static readonly Dictionary<string, string> Templates = new()
    {
        ["basic"] = """
import time
import logging

logging.basicConfig(level=logging.INFO, format='%(asctime)s [%(levelname)s] %(message)s')
logger = logging.getLogger(__name__)

def main():
    logger.info("Bot started!")
    for i in range(10):
        logger.info(f"Processing item {i + 1}/10...")
        time.sleep(1)
    logger.info("Bot finished successfully.")

if __name__ == '__main__':
    main()
""",
        ["loop"] = """
import time
import logging
from datetime import datetime

logging.basicConfig(level=logging.INFO, format='%(asctime)s [%(levelname)s] %(message)s')
logger = logging.getLogger(__name__)

INTERVAL = 5  # seconds

def task():
    logger.info(f"Running task at {datetime.now().strftime('%H:%M:%S')}")

def main():
    logger.info("Loop bot started.")
    while True:
        try:
            task()
            time.sleep(INTERVAL)
        except KeyboardInterrupt:
            logger.info("Bot stopped by user.")
            break

if __name__ == '__main__':
    main()
""",
        ["blank"] = """
def main():
    pass

if __name__ == '__main__':
    main()
"""
    };

    private static readonly Dictionary<string, string> RequirementsTemplates = new()
    {
        ["basic"] = "# No extra requirements\n",
        ["loop"] = "# No extra requirements\n",
        ["blank"] = "# Add your dependencies here\n",
    };

    public BotRepository(IConfiguration config, ILogger<BotRepository> logger)
    {
        _logger = logger;
        _botsBasePath = config["BotsPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "bots");
        Directory.CreateDirectory(_botsBasePath);
        LoadExistingBots();
    }

    private void LoadExistingBots()
    {
        foreach (var dir in Directory.GetDirectories(_botsBasePath))
        {
            var name = Path.GetFileName(dir);
            var bot = new Bot
            {
                Id = name,
                Name = name,
                Description = ReadMetadata(dir),
                DirectoryPath = dir,
                CreatedAt = Directory.GetCreationTime(dir),
            };
            _bots[bot.Id] = bot;
            _logger.LogInformation("Loaded bot: {Name}", name);
        }
    }

    private string ReadMetadata(string dir)
    {
        var metaPath = Path.Combine(dir, ".meta");
        return File.Exists(metaPath) ? File.ReadAllText(metaPath) : "";
    }

    public IEnumerable<Bot> GetAll() => _bots.Values.OrderBy(b => b.CreatedAt);

    public Bot? GetById(string id) => _bots.TryGetValue(id, out var bot) ? bot : null;

    public Bot Create(CreateBotRequest request)
    {
        var id = request.Name.ToLower().Replace(" ", "_");
        var botDir = Path.Combine(_botsBasePath, id);

        if (Directory.Exists(botDir))
            throw new InvalidOperationException($"Bot '{id}' already exists.");

        Directory.CreateDirectory(botDir);

        // Write bot.py
        var template = Templates.GetValueOrDefault(request.Template, Templates["basic"]);
        File.WriteAllText(Path.Combine(botDir, "bot.py"), template);

        // Write requirements.txt
        var reqs = RequirementsTemplates.GetValueOrDefault(request.Template, "");
        File.WriteAllText(Path.Combine(botDir, "requirements.txt"), reqs);

        // Write description metadata
        if (!string.IsNullOrEmpty(request.Description))
            File.WriteAllText(Path.Combine(botDir, ".meta"), request.Description);

        var bot = new Bot
        {
            Id = id,
            Name = id,
            Description = request.Description,
            DirectoryPath = botDir,
        };

        _bots[id] = bot;
        return bot;
    }

    public void Delete(string id)
    {
        if (!_bots.TryRemove(id, out var bot)) return;
        if (Directory.Exists(bot.DirectoryPath))
            Directory.Delete(bot.DirectoryPath, recursive: true);
    }

    public void UpdateStatus(string id, BotStatus status, string? containerId = null)
    {
        if (_bots.TryGetValue(id, out var bot))
        {
            bot.Status = status;
            bot.ContainerId = containerId;
            if (status == BotStatus.Running)
                bot.LastRun = DateTime.UtcNow;
        }
    }

    public string GetFilePath(string botId, string filename)
    {
        var bot = GetById(botId) ?? throw new KeyNotFoundException($"Bot '{botId}' not found.");
        // Only allow bot.py and requirements.txt for security
        if (filename != "bot.py" && filename != "requirements.txt")
            throw new UnauthorizedAccessException("Only bot.py and requirements.txt can be edited.");
        return Path.Combine(bot.DirectoryPath, filename);
    }

    public string GetBotDirectory(string botId)
    {
        var bot = GetById(botId) ?? throw new KeyNotFoundException($"Bot '{botId}' not found.");
        return bot.DirectoryPath;
    }
}
