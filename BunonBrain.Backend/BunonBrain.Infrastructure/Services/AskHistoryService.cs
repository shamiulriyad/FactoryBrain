using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Dtos.Ask;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Services;

/// <summary>
/// Saved Ask BunonBrain chats (<c>ask_conversations</c> / <c>ask_messages</c>).
/// Each query filters by the owner, so another user's chat is "not found".
/// Bounded: at most <see cref="MaxConversationsPerUser"/> chats per user (the
/// oldest are removed) and <see cref="MaxMessagesPerConversation"/> questions per
/// chat (a full chat continues in a new one).
/// </summary>
public sealed class AskHistoryService : IAskHistoryService
{
    public const int MaxConversationsPerUser = 100;
    public const int MaxMessagesPerConversation = 200;
    public const int MaxListLimit = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BunonBrainDbContext _db;

    public AskHistoryService(BunonBrainDbContext db) { _db = db; }

    public async Task<(Guid ConversationId, Guid MessageId)> AppendAsync(
        Guid userId, Guid? conversationId, string question, AskAnswerResponse answer, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        AskConversation? conversation = null;
        if (conversationId is { } id)
        {
            conversation = await _db.AskConversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
            if (conversation is not null
                && await _db.AskMessages.CountAsync(m => m.ConversationId == id, ct) >= MaxMessagesPerConversation)
                conversation = null;
        }

        var created = conversation is null;
        if (created)
        {
            conversation = new AskConversation { UserId = userId, Title = Title(question), CreatedAt = now, UpdatedAt = now };
            _db.AskConversations.Add(conversation);
        }
        else
        {
            conversation!.UpdatedAt = now;
        }

        var message = new AskMessage
        {
            ConversationId = conversation.Id,
            Question = question.Trim(),
            Answer = answer.Finding,
            Mode = answer.Mode,
            Confidence = answer.Confidence,
            CitationsJson = JsonSerializer.Serialize(answer.Citations, Json),
            Cached = answer.Cached,
            CachedQuestion = answer.Cached ? answer.CachedQuestion : null,
            CreatedAt = now,
        };
        _db.AskMessages.Add(message);
        await _db.SaveChangesAsync(ct);

        if (created) await TrimAsync(userId, ct);
        return (conversation.Id, message.Id);
    }

    public async Task<IReadOnlyList<AskConversationSummary>> ListAsync(Guid userId, int limit, CancellationToken ct) =>
        await _db.AskConversations.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(Math.Clamp(limit, 1, MaxListLimit))
            .Select(c => new AskConversationSummary(
                c.Id, c.Title, c.CreatedAt, c.UpdatedAt,
                c.Messages.Count,
                c.Messages.OrderByDescending(m => m.CreatedAt).Select(m => m.Question).FirstOrDefault()))
            .ToListAsync(ct);

    public async Task<AskConversationDetail?> GetAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var c = await _db.AskConversations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == conversationId && x.UserId == userId, ct);
        if (c is null) return null;
        var messages = await _db.AskMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return new AskConversationDetail(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, messages.Select(ToDto).ToList());
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid conversationId, CancellationToken ct) =>
        await _db.AskConversations.Where(c => c.Id == conversationId && c.UserId == userId).ExecuteDeleteAsync(ct) > 0;

    /// <summary>The first question as a one-line title, cut at a word boundary.</summary>
    internal static string Title(string question)
    {
        var oneLine = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var max = AskConversationConfiguration.TitleMax;
        if (oneLine.Length <= max) return oneLine.Length == 0 ? "Question" : oneLine;
        var cut = oneLine.LastIndexOf(' ', max - 1);
        return (cut > max / 2 ? oneLine[..cut] : oneLine[..(max - 1)]) + "…";
    }

    internal static AskMessageDto ToDto(AskMessage m) => new(
        m.Id, m.Question, m.Answer, m.Mode, m.Confidence, Citations(m.CitationsJson), m.Cached, m.CachedQuestion, m.CreatedAt);

    private static IReadOnlyList<CitationDto> Citations(string json)
    {
        try { return JsonSerializer.Deserialize<List<CitationDto>>(json, Json) ?? new List<CitationDto>(); }
        catch (JsonException) { return Array.Empty<CitationDto>(); }
    }

    /// <summary>Keep only the user's newest chats.</summary>
    private async Task TrimAsync(Guid userId, CancellationToken ct)
    {
        var stale = await _db.AskConversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Skip(MaxConversationsPerUser)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (stale.Count > 0)
            await _db.AskConversations.Where(c => stale.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }
}
