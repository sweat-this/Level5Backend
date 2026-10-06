using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Level5.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class NotificationsFlowTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Authentication_is_required()
    {
        var response = await factory.CreateClient().GetAsync("/api/v2/platform/me/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Friend_request_creates_one_private_safe_notification_and_duplicate_creates_no_second()
    {
        var sender = await factory.RegisterNewPlayerAsync("NotifyApiSender");
        var recipient = await factory.RegisterNewPlayerAsync("NotifyApiRecipient");

        var created = await sender.Client.PostAsJsonAsync(
            "/api/v2/friends/requests", new { toPlayerId = recipient.PlayerId });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var duplicate = await sender.Client.PostAsJsonAsync(
            "/api/v2/friends/requests", new { toPlayerId = recipient.PlayerId });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var recipientPage = await GetPageAsync(recipient.Client);
        var item = Assert.Single(recipientPage.GetProperty("items").EnumerateArray());
        Assert.Equal("platform", item.GetProperty("source").GetString());
        Assert.Equal("friend-request-received", item.GetProperty("kind").GetString());
        Assert.Equal("New friend request", item.GetProperty("title").GetString());
        Assert.Equal("You received a new friend request.", item.GetProperty("body").GetString());
        Assert.Equal("/account/friends", item.GetProperty("actionPath").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
        Assert.False(item.TryGetProperty("recipientPlayerId", out _));
        Assert.False(item.TryGetProperty("sourceEventKey", out _));
        Assert.False(item.TryGetProperty("accountId", out _));

        var senderPage = await GetPageAsync(sender.Client);
        Assert.Empty(senderPage.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Paging_read_unread_and_not_found_isolation_are_supported()
    {
        var recipient = await factory.RegisterNewPlayerAsync("NotifyApiPaging");
        var senderOne = await factory.RegisterNewPlayerAsync("NotifyApiOne");
        var senderTwo = await factory.RegisterNewPlayerAsync("NotifyApiTwo");
        await senderOne.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = recipient.PlayerId });
        await senderTwo.Client.PostAsJsonAsync("/api/v2/friends/requests", new { toPlayerId = recipient.PlayerId });

        var first = await GetPageAsync(recipient.Client, "?limit=1");
        var notification = Assert.Single(first.GetProperty("items").EnumerateArray());
        var notificationId = notification.GetProperty("id").GetGuid();
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var second = await GetPageAsync(recipient.Client, $"?limit=1&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        var read = await recipient.Client.PatchAsJsonAsync(
            $"/api/v2/platform/me/notifications/{notificationId}", new { isRead = true });
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        var readAgain = await recipient.Client.PatchAsJsonAsync(
            $"/api/v2/platform/me/notifications/{notificationId}", new { isRead = true });
        Assert.Equal(HttpStatusCode.NoContent, readAgain.StatusCode);
        var afterRead = await GetPageAsync(recipient.Client, "?limit=100");
        var readItem = Assert.Single(
            afterRead.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == notificationId);
        Assert.NotEqual(JsonValueKind.Null, readItem.GetProperty("readAt").ValueKind);

        var unread = await recipient.Client.PatchAsJsonAsync(
            $"/api/v2/platform/me/notifications/{notificationId}", new { isRead = false });
        Assert.Equal(HttpStatusCode.NoContent, unread.StatusCode);
        var unreadAgain = await recipient.Client.PatchAsJsonAsync(
            $"/api/v2/platform/me/notifications/{notificationId}", new { isRead = false });
        Assert.Equal(HttpStatusCode.NoContent, unreadAgain.StatusCode);

        var foreign = await senderOne.Client.PatchAsJsonAsync(
            $"/api/v2/platform/me/notifications/{notificationId}", new { isRead = true });
        var missing = await senderOne.Client.PatchAsJsonAsync(
            $"/api/v2/platform/me/notifications/{Guid.NewGuid()}", new { isRead = true });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(missing.StatusCode, foreign.StatusCode);
    }

    [Fact]
    public async Task Invalid_cursor_and_page_size_are_validation_errors()
    {
        var player = await factory.RegisterNewPlayerAsync("NotifyApiInvalid");

        var invalidCursor = await player.Client.GetAsync(
            "/api/v2/platform/me/notifications?cursor=not-a-cursor");
        var invalidLimit = await player.Client.GetAsync(
            "/api/v2/platform/me/notifications?limit=101");

        Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
    }

    [Fact]
    public async Task Read_state_request_requires_isRead()
    {
        var player = await factory.RegisterNewPlayerAsync("NotifyApiBody");

        var response = await player.Client.PatchAsJsonAsync(
            $"/api/v2/platform/me/notifications/{Guid.NewGuid()}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Read_state_openapi_contract_advertises_runtime_no_content_status()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        var responses = document
            .GetProperty("paths")
            .GetProperty("/api/v2/platform/me/notifications/{notificationId}")
            .GetProperty("patch")
            .GetProperty("responses");

        Assert.True(responses.TryGetProperty("204", out _));
        Assert.False(responses.TryGetProperty("200", out _));
    }

    private static async Task<JsonElement> GetPageAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/v2/platform/me/notifications{query}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }
}
