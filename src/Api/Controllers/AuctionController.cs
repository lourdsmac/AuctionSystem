using System.Text;
using System.Text.Json;
using AuctionSystem.Application.Dtos;
using AuctionSystem.Application.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace AuctionSystem.Api.Controllers;

/// <summary>
/// Thin HTTP adapter: JSON snapshot + SSE stream. Business rules live in Application/Domain.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class AuctionController : ControllerBase
{
    private static readonly JsonSerializerOptions SseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AuctionService _auctionService;
    private readonly ILogger<AuctionController> _logger;

    public AuctionController(AuctionService auctionService, ILogger<AuctionController> logger)
    {
        _auctionService = auctionService;
        _logger = logger;
    }

    /// <summary>Current auction snapshot (shared by SSE and WebSocket flows).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(AuctionStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuctionStateDto>> Get(CancellationToken cancellationToken)
    {
        var dto = await _auctionService.GetCurrentAsync(cancellationToken);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Server-Sent Events: one-way stream (server → browser). Pushes a JSON snapshot every 2 seconds.
    /// Uses <c>text/event-stream</c> and flushes through <see cref="System.IO.Pipelines.PipeWriter"/> (Kestrel).
    /// </summary>
    [HttpGet("sse")]
    public async Task GetSse(CancellationToken cancellationToken)
    {
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        // SSE + HTTP keep-alive: avoid caches/proxies buffering the stream; hint connection reuse.
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        _logger.LogInformation("SSE client connected from {RemoteIp}", HttpContext.Connection.RemoteIpAddress);

        var writer = Response.BodyWriter;
        // Comment line (starts with ':') — heartbeats / connection confirmation; ignored by EventSource API.
        await writer.WriteAsync(Encoding.UTF8.GetBytes(": sse-connected\n\n"), cancellationToken);
        await writer.FlushAsync(cancellationToken);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var dto = await _auctionService.GetCurrentAsync(cancellationToken);
                if (dto is null)
                {
                    break;
                }

                var payload =
                    $"data: {JsonSerializer.Serialize(dto, SseJson)}\n\n";

                await writer.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE client disconnected.");
        }
    }
}
