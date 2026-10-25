using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/ask")]
public class AskController : ControllerBase
{
    private readonly IAskService _svc;
    private readonly IPyRagClient _rag;

    public AskController(IAskService svc, IPyRagClient rag)
    { _svc = svc; _rag = rag; }

    /// <summary>POST /api/ask — answered by the Python RAG service.</summary>
    /// <remarks>
    /// Body validation runs in the global <c>FluentValidationFilter</c>
    /// (400 ValidationProblemDetails). 503 when the RAG service can't answer;
    /// there is no substitute answer.
    /// </remarks>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] AskRequest body, CancellationToken ct)
    {
        try
        {
            return Ok(await _svc.AskAsync(body, ct));
        }
        catch (PyRagUnavailableException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The RAG service is unavailable", detail: ex.Message);
        }
    }

    /// <summary>GET /api/ask/status — which RAG service, collection and models answer questions, and whether it is up.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var o = _rag.Options;
        var h = await _rag.HealthAsync(ct);
        return Ok(new
        {
            service = "python-rag",
            reachable = h.Reachable,
            ready = h.Ready,
            qdrant = h.Qdrant,
            collection = o.Collection,
            llmModel = o.LlmModel,
            embeddingProvider = o.EmbeddingProvider,
            embeddingModel = o.EmbeddingModel,
            error = h.Error
        });
    }
}
