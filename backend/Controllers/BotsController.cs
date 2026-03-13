// ─── Controllers/BotsController.cs ───────────────────────────

using BotPanel.Models;
using BotPanel.Services;
using Microsoft.AspNetCore.Mvc;

namespace BotPanel.Controllers;

[ApiController]
[Route("api/bots")]
public class BotsController : ControllerBase
{
    private readonly IBotRepository _repo;
    private readonly IDockerService _docker;
    private readonly ILogStreamingService _logStreaming;
    private readonly ILogger<BotsController> _logger;

    // Per-bot cancellation tokens for stopping containers
    private static readonly Dictionary<string, CancellationTokenSource> _runTokens = new();

    public BotsController(
        IBotRepository repo,
        IDockerService docker,
        ILogStreamingService logStreaming,
        ILogger<BotsController> logger)
    {
        _repo = repo;
        _docker = docker;
        _logStreaming = logStreaming;
        _logger = logger;
    }

    // ─── GET /api/bots ───────────────────────────────────────
    [HttpGet]
    public ActionResult<IEnumerable<BotDto>> GetAll()
    {
        return Ok(_repo.GetAll().Select(ToDto));
    }

    // ─── GET /api/bots/{id} ──────────────────────────────────
    [HttpGet("{id}")]
    public ActionResult<BotDto> GetById(string id)
    {
        var bot = _repo.GetById(id);
        if (bot == null) return NotFound();
        return Ok(ToDto(bot));
    }

    // ─── POST /api/bots ──────────────────────────────────────
    [HttpPost]
    public ActionResult<BotDto> Create([FromBody] CreateBotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Bot name is required." });

        // Sanitize name
        request.Name = System.Text.RegularExpressions.Regex.Replace(
            request.Name.ToLower(), @"[^a-z0-9_]", "_");

        try
        {
            var bot = _repo.Create(request);
            return CreatedAtAction(nameof(GetById), new { id = bot.Id }, ToDto(bot));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // ─── DELETE /api/bots/{id} ───────────────────────────────
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var bot = _repo.GetById(id);
        if (bot == null) return NotFound();

        // Stop container if running
        if (bot.Status == BotStatus.Running && bot.ContainerId != null)
        {
            _logStreaming.StopStreaming(id);
            await _docker.StopBotAsync(bot.ContainerId);
        }

        _repo.Delete(id);
        return NoContent();
    }

    // ─── GET /api/bots/{id}/files/{filename} ─────────────────
    [HttpGet("{id}/files/{filename}")]
    public IActionResult GetFile(string id, string filename)
    {
        try
        {
            var path = _repo.GetFilePath(id, filename);
            if (!System.IO.File.Exists(path))
                return Ok(new BotFileContent { Content = "" });

            var content = System.IO.File.ReadAllText(path);
            return Ok(new BotFileContent { Content = content });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ─── PUT /api/bots/{id}/files/{filename} ─────────────────
    [HttpPut("{id}/files/{filename}")]
    public IActionResult SaveFile(string id, string filename, [FromBody] BotFileContent body)
    {
        try
        {
            var path = _repo.GetFilePath(id, filename);
            System.IO.File.WriteAllText(path, body.Content);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ─── POST /api/bots/{id}/run ─────────────────────────────
    [HttpPost("{id}/run")]
    public async Task<IActionResult> Run(string id)
    {
        var bot = _repo.GetById(id);
        if (bot == null) return NotFound();

        if (bot.Status == BotStatus.Running)
            return Conflict(new { message = "Bot is already running." });

        try
        {
            _repo.UpdateStatus(id, BotStatus.Starting);

            var cts = new CancellationTokenSource();
            _runTokens[id] = cts;

            var containerId = await _docker.RunBotAsync(id, bot.DirectoryPath, cts.Token);
            _repo.UpdateStatus(id, BotStatus.Running, containerId);

            // Start streaming logs in background
            await _logStreaming.StartStreamingAsync(id, containerId, cts.Token);

            _logger.LogInformation("Bot {Id} started in container {ContainerId}", id, containerId[..12]);
            return Ok(new { containerId });
        }
        catch (Exception ex)
        {
            _repo.UpdateStatus(id, BotStatus.Error);
            _logger.LogError(ex, "Failed to start bot {Id}", id);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // ─── POST /api/bots/{id}/stop ────────────────────────────
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id)
    {
        var bot = _repo.GetById(id);
        if (bot == null) return NotFound();

        if (bot.Status != BotStatus.Running || bot.ContainerId == null)
            return BadRequest(new { message = "Bot is not running." });

        try
        {
            _logStreaming.StopStreaming(id);

            if (_runTokens.TryGetValue(id, out var cts))
            {
                cts.Cancel();
                _runTokens.Remove(id);
            }

            await _docker.StopBotAsync(bot.ContainerId);
            _repo.UpdateStatus(id, BotStatus.Stopped, null);

            _logger.LogInformation("Bot {Id} stopped", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop bot {Id}", id);
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // ─── POST /api/bots/{id}/restart ─────────────────────────
    [HttpPost("{id}/restart")]
    public async Task<IActionResult> Restart(string id)
    {
        await Stop(id);
        await Task.Delay(500);
        return await Run(id);
    }

    // ─── Helper ──────────────────────────────────────────────
    private static BotDto ToDto(Bot bot) => new()
    {
        Id = bot.Id,
        Name = bot.Name,
        Description = bot.Description,
        Status = bot.Status.ToString().ToLower(),
        ContainerId = bot.ContainerId?[..Math.Min(12, bot.ContainerId.Length)],
        CreatedAt = bot.CreatedAt,
        LastRun = bot.LastRun,
    };
}
