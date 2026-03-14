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

    // ─── GET /api/bots/running ───────────────────────────────
    // Returns only bots that are currently running.
    // Used by the Multi-Console view to know which bots to subscribe to.
    [HttpGet("running")]
    public ActionResult<IEnumerable<BotDto>> GetRunning()
    {
        var running = _repo.GetAll()
            .Where(b => b.Status == BotStatus.Running)
            .Select(ToDto);
        return Ok(running);
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

    // ─── PATCH /api/bots/{id}/description ───────────────────
    [HttpPatch("{id}/description")]
    public IActionResult UpdateDescription(string id, [FromBody] UpdateDescriptionRequest request)
    {
        var bot = _repo.GetById(id);
        if (bot == null) return NotFound();
        _repo.UpdateDescription(id, request.Description ?? "");
        return NoContent();
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

    // ─── GET /api/bots/{id}/logs ─────────────────────────────
    // Polling endpoint: returns recent log lines from the container.
    // Optional ?since=<timestamp> to get only new lines.
    [HttpGet("{id}/logs")]
    public async Task<IActionResult> GetLogs(string id, [FromQuery] string? since = null)
    {
        var bot = _repo.GetById(id);
        if (bot == null) return NotFound();
        if (bot.ContainerId == null)
            return Ok(new { lines = Array.Empty<object>(), status = "stopped" });

        try
        {
            // Run `docker logs` with optional --since
            var args = since != null
                ? $"logs --timestamps --since \"{since}\" {bot.ContainerId}"
                : $"logs --timestamps --tail 100 {bot.ContainerId}";

            var psi = new System.Diagnostics.ProcessStartInfo("docker", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var lines = new List<object>();

            void ParseLines(string raw, string stream)
            {
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // Docker timestamps: 2025-01-01T12:00:00.000000000Z text
                    string timestamp = "";
                    string text = line;
                    if (line.Length > 32 && line[10] == 'T')
                    {
                        var spaceIdx = line.IndexOf(' ');
                        if (spaceIdx >= 0)
                        {
                            timestamp = line[..spaceIdx];
                            text = line[(spaceIdx + 1)..];
                        }
                        else
                        {
                            // Whole line is just a timestamp — discard
                            continue;
                        }
                    }
                    // Discard lines whose text is itself a bare ISO timestamp
                    if (IsIsoTimestamp(text.Trim())) continue;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    lines.Add(new { stream, text, timestamp });
                }
            }

            ParseLines(stdout, "stdout");
            ParseLines(stderr, "stderr");

            // Check if container is still running
            var inspectPsi = new System.Diagnostics.ProcessStartInfo(
                "docker", $"inspect --format={{{{.State.Running}}}} {bot.ContainerId}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var inspectProc = System.Diagnostics.Process.Start(inspectPsi)!;
            var running = (await inspectProc.StandardOutput.ReadToEndAsync()).Trim();
            await inspectProc.WaitForExitAsync();

            var status = running == "true" ? "running" : "stopped";
            if (status == "stopped") _repo.UpdateStatus(id, BotStatus.Stopped, null);

            return Ok(new { lines, status });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get logs for bot {Id}", id);
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
    // Matches bare ISO-8601 timestamps emitted by Docker (e.g. 2026-03-13T21:35:27.685621012Z)
    private static bool IsIsoTimestamp(string s) =>
        s.Length >= 20 && s.Length <= 35 &&
        s[4] == '-' && s[7] == '-' && s[10] == 'T' &&
        s[13] == ':' && s[16] == ':' &&
        (s[^1] == 'Z' || s[^1] == 'z');

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

public class UpdateDescriptionRequest
{
    public string? Description { get; set; }
}
