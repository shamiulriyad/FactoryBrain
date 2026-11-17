using BunonBrain.Api.Authorization;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Ask;
using BunonBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace BunonBrain.Api.Controllers;

[ApiController]
[Route("api/ask")]
public class AskController : ControllerBase
{
    private readonly IAskService _svc;
    private readonly IPyRagClient _rag;
    private readonly IAskHistoryService _history;
    private readonly ILogger<AskController> _log;

    public AskController(IAskService svc, IPyRagClient rag, IAskHistoryService history, ILogger<AskController> log)
    { _svc = svc; _rag = rag; _history = history; _log = log; }

    /// <summary>POST /api/ask — answered by the Python RAG service.</summary>
    /// <remarks>
    /// Body validation runs in the global <c>FluentValidationFilter</c>
    /// (400 ValidationProblemDetails). 503 when the RAG service can't answer;
    /// there is no substitute answer.
    /// </remarks>
    [HttpPost]
    [RequirePermission(Permissions.KnowledgeView)]
    public async Task<IActionResult> Post([FromBody] AskRequest body, CancellationToken ct)
    {
        AskAnswerResponse answer;
        try
        {
            answer = await _svc.AskAsync(body, ct);
        }
        catch (PyRagUnavailableException ex)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The RAG service is unavailable", detail: ex.Message);
        }

        // Save it to the caller's chat history. A history problem never costs the answer.
        try
        {
            var (conversationId, messageId) = await _history.AppendAsync(this.Caller().UserId, body.ConversationId, body.Query, answer, ct);
            answer = answer with { ConversationId = conversationId, MessageId = messageId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Ask history could not be saved.");
        }
        return Ok(answer);
    }

    /// <summary>GET /api/ask/conversations — the caller's saved chats, most recent first.</summary>
    [HttpGet("conversations")]
    [RequirePermission(Permissions.KnowledgeView)]
    public async Task<IActionResult> Conversations([FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await _history.ListAsync(this.Caller().UserId, limit, ct));

    /// <summary>GET /api/ask/conversations/{id} — one of the caller's chats with every question and answer.</summary>
    [HttpGet("conversations/{conversationId}")]
    [RequirePermission(Permissions.KnowledgeView)]
    public async Task<IActionResult> Conversation(Guid conversationId, CancellationToken ct) =>
        await _history.GetAsync(this.Caller().UserId, conversationId, ct) is { } detail
            ? Ok(detail)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Chat not found");

    /// <summary>DELETE /api/ask/conversations/{id} — delete one of the caller's chats.</summary>
    [HttpDelete("conversations/{conversationId}")]
    [RequirePermission(Permissions.KnowledgeView)]
    public async Task<IActionResult> DeleteConversation(Guid conversationId, CancellationToken ct) =>
        await _history.DeleteAsync(this.Caller().UserId, conversationId, ct)
            ? NoContent()
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Chat not found");

    /// <summary>GET /api/ask/status — which RAG service, collection and models answer questions, and whether it is up.</summary>
    [HttpGet("status")]
    [RequirePermission(Permissions.KnowledgeView)]
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
            // What the service says it runs wins over this API's PYRAG_LLM_MODEL guess.
            llmModel = h.LlmModel ?? o.LlmModel,
            llmProvider = h.LlmProvider,
            embeddingProvider = o.EmbeddingProvider,
            embeddingModel = o.EmbeddingModel,
            error = h.Error
        });
    }
}
