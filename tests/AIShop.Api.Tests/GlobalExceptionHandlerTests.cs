using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AIShop.Api.Middleware;

namespace AIShop.Api.Tests;

public sealed class GlobalExceptionHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task TryHandleAsync_ReturnsTrue()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var result = await handler.TryHandleAsync(context, new InvalidOperationException("test"), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task TryHandleAsync_SetsStatusCode500()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await handler.TryHandleAsync(context, new InvalidOperationException("test"), CancellationToken.None);

        Assert.Equal(500, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_SetsContentType()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await handler.TryHandleAsync(context, new InvalidOperationException("test"), CancellationToken.None);

        // WriteAsJsonAsync sets application/json; charset=utf-8
        Assert.Contains("application/json", context.Response.ContentType);
    }

    [Fact]
    public async Task TryHandleAsync_ReturnsProblemDetails()
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test";
        context.Response.Body = new MemoryStream();

        await handler.TryHandleAsync(context, new InvalidOperationException("test message"), CancellationToken.None);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);

        Assert.NotNull(problem);
        Assert.Equal(500, problem!.Status);
        Assert.Equal("Internal Server Error", problem.Title);
        Assert.Equal("An unexpected error occurred. Please try again later.", problem.Detail);
        Assert.Equal("/api/test", problem.Instance);
        Assert.Equal("https://tools.ietf.org/html/rfc9457", problem.Type);
    }
}
