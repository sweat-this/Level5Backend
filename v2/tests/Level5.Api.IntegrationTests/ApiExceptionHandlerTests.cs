using System.Text.Json;
using Level5.Api.ErrorHandling;
using Level5.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Level5.Api.IntegrationTests;

public sealed class ApiExceptionHandlerTests
{
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
