using System.Text.Json;
using Level5.Api.ErrorHandling;
using Level5.Application.Common;
using Level5.Application.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Level5.Api.IntegrationTests;

public sealed class ApiExceptionHandlerTests
{
    [Theory]
    [InlineData("invalid_chat_message", 400)][InlineData("invalid_chat_cursor", 400)][InlineData("invalid_chat_limit", 400)]
    [InlineData("invalid_chat_read_position", 400)][InlineData("invalid_chat_report", 400)]
    [InlineData("chat_communication_restricted", 403)][InlineData("chat_read_only", 409)]
    [InlineData("chat_message_conflict", 409)][InlineData("chat_report_already_exists", 409)]
    [InlineData("chat_rate_limited", 429)][InlineData("unknown_chat_error", 500)]
    public async Task Chat_errors_have_explicit_stable_statuses_and_rate_delay_is_rounded_up(string code, int status)
    {
        var (context, _) = await HandleAsync(new Level5.Application.BloodMoney.BloodMoneyChatException(code,
            "Safe server message.", TimeSpan.FromMilliseconds(1101)));
        Assert.Equal(status, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Equal(status == 500 ? "internal_error" : code, body.GetProperty("code").GetString());
        if (status == 429) Assert.Equal("2", context.Response.Headers.RetryAfter.ToString());
        else Assert.False(context.Response.Headers.ContainsKey("Retry-After"));
    }
    [Fact]
    public async Task A_translated_conflict_is_reported_as_409_without_leaking_database_details()
    {
        // Infrastructure translates a unique-constraint violation into ConflictException before
        // it reaches the HTTP layer (see ConflictTranslatingSave), so this is what the handler
        // actually sees for a lost uniqueness race.
        var (context, logger) = await HandleAsync(new ConflictException("The request conflicts with existing data. Please retry."));

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        var body = await ReadBodyAsync(context);
        Assert.Equal("conflict", body.GetProperty("code").GetString());
        Assert.DoesNotContain("constraint", body.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task A_database_failure_that_is_not_a_conflict_is_a_logged_500_not_a_409()
    {
        // Data truncation, check/not-null violations, deadlocks and statement timeouts all
        // arrive as DbUpdateException. Reporting these as a retryable 409 would both mislead the
        // client and - because only 500s are logged - make them vanish from the error logs.
        var dbException = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new InvalidOperationException("22001: value too long for type character varying(512)"));

        var (context, logger) = await HandleAsync(dbException);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        var body = await ReadBodyAsync(context);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", body.GetProperty("title").GetString());
        Assert.DoesNotContain("22001", body.ToString());
        Assert.Contains(logger.Errors, e => ReferenceEquals(e, dbException));
    }

    [Fact]
    public async Task A_database_outage_that_outlived_the_retry_budget_is_a_logged_503_without_details()
    {
        var outage = new RetryLimitExceededException(
            "The maximum number of retries (3) was exceeded while executing database operations.",
            new InvalidOperationException("Failed to connect to 10.0.0.5:5432"));

        var (context, logger) = await HandleAsync(outage);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        var body = await ReadBodyAsync(context);
        Assert.Equal("service_unavailable", body.GetProperty("code").GetString());
        Assert.DoesNotContain("10.0.0.5", body.ToString());
        Assert.DoesNotContain("retries", body.ToString());
        Assert.Contains(logger.Errors, e => ReferenceEquals(e, outage));
    }

    [Fact]
    public async Task Unconfigured_email_delivery_is_an_explicit_503_without_secret_details()
    {
        var unavailable = new EmailVerificationDeliveryUnavailableException();

        var (context, logger) = await HandleAsync(unavailable);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var body = await ReadBodyAsync(context);
        Assert.Equal("email_verification_delivery_unavailable", body.GetProperty("code").GetString());
        Assert.Equal("The service is temporarily unavailable. Please retry.", body.GetProperty("title").GetString());
        Assert.Contains(logger.Errors, e => ReferenceEquals(e, unavailable));
    }

    private static async Task<(DefaultHttpContext Context, CapturingLogger Logger)> HandleAsync(Exception exception)
    {
        var logger = new CapturingLogger();
        var handler = new ApiExceptionHandler(logger);
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        Assert.True(handled);

        return (context, logger);
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
    }

    private sealed class CapturingLogger : ILogger<ApiExceptionHandler>
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null)
            {
                Errors.Add(exception);
            }
        }
    }
}
