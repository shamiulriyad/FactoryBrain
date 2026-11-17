using System.Net;
using System.Text.Json;
using BunonBrain.Infrastructure.Services;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Ask BunonBrain chat history (saved per user) and the reused-answer flag,
/// against the fake RAG service of <see cref="DbApiFactory"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAskHistoryTests
{
    private readonly PostgresStack _stack;
    public DbAskHistoryTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    private static async Task<JsonElement> Ask(HttpClient client, object body)
    {
        using var res = await client.PostAsync("/api/ask", Rag.Json(body));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> Get(HttpClient client, string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var res = await client.GetAsync(path);
        Assert.Equal(expected, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [DockerFact]
    public async Task Questions_are_saved_as_a_chat_that_continues_and_reopens()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);

        var first = await Ask(viewer, new { query = "history test: when do I replace a bearing?" });
        var chatId = first.GetProperty("conversationId").GetGuid();
        var second = await Ask(viewer, new { query = "history test: and the belt?", conversationId = chatId });
        Assert.Equal(chatId, second.GetProperty("conversationId").GetGuid());

        var list = await Get(viewer, "/api/ask/conversations");
        var item = list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chatId);
        Assert.Equal("history test: when do I replace a bearing?", item.GetProperty("title").GetString());
        Assert.Equal(2, item.GetProperty("messageCount").GetInt32());
        Assert.Equal("history test: and the belt?", item.GetProperty("lastQuestion").GetString());

        var detail = await Get(viewer, $"/api/ask/conversations/{chatId}");
        var messages = detail.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("history test: when do I replace a bearing?", messages[0].GetProperty("question").GetString());
        Assert.Equal(DbApiFactory.FakeRagAnswer, messages[0].GetProperty("answer").GetString());
        Assert.Equal($"{DbApiFactory.FakeRagSourceFile} · page 2",
            messages[0].GetProperty("citations")[0].GetProperty("title").GetString());

        using var del = await viewer.DeleteAsync($"/api/ask/conversations/{chatId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await Get(viewer, $"/api/ask/conversations/{chatId}", HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_chat_is_private_to_its_owner()
    {
        using var owner = Rag.Client(Api, Roles.Viewer);
        using var other = Rag.Client(Api, Roles.Admin);
        var chatId = (await Ask(owner, new { query = "history test: private question" })).GetProperty("conversationId").GetGuid();

        await Get(other, $"/api/ask/conversations/{chatId}", HttpStatusCode.NotFound);
        Assert.DoesNotContain((await Get(other, "/api/ask/conversations")).EnumerateArray(), c => c.GetProperty("id").GetGuid() == chatId);
        using (var del = await other.DeleteAsync($"/api/ask/conversations/{chatId}"))
            Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);

        // Someone else's chat id can't be used to post into it: a new chat is started instead.
        var intruder = await Ask(other, new { query = "history test: intruding", conversationId = chatId });
        Assert.NotEqual(chatId, intruder.GetProperty("conversationId").GetGuid());
        var ownerDetail = await Get(owner, $"/api/ask/conversations/{chatId}");
        Assert.Single(ownerDetail.GetProperty("messages").EnumerateArray());
    }

    [DockerFact]
    public async Task A_reused_answer_is_flagged_and_fresh_skips_the_cache()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        var q = $"history test: {DbApiFactory.RagCachedMarker} bearing";

        var reused = await Ask(viewer, new { query = q });
        Assert.True(reused.GetProperty("cached").GetBoolean());
        Assert.Equal("the earlier question", reused.GetProperty("cachedQuestion").GetString());
        Assert.True(Api.RagUseCache.Last());

        var fresh = await Ask(viewer, new { query = q, fresh = true });
        Assert.False(fresh.GetProperty("cached").GetBoolean());
        Assert.False(Api.RagUseCache.Last());

        var detail = await Get(viewer, $"/api/ask/conversations/{reused.GetProperty("conversationId").GetGuid()}");
        Assert.True(detail.GetProperty("messages")[0].GetProperty("cached").GetBoolean());
    }

    [Fact]
    public void Chat_titles_are_one_line_and_cut_at_a_word()
    {
        Assert.Equal("fabric inspection kivabe kora hoy?", AskHistoryService.Title("  fabric inspection\nkivabe   kora hoy? "));
        var title = AskHistoryService.Title(string.Join(' ', Enumerable.Repeat("inspection", 40)));
        Assert.True(title.Length <= 120);
        Assert.EndsWith("…", title);
        Assert.DoesNotContain("inspectio…", title);
    }
}
