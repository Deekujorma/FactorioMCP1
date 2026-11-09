using System.Net;
using System.Security.Cryptography;
using System.Text;
using FactorioMCP.Screenshots;
using Microsoft.AspNetCore.Http.Timeouts;

namespace FactorioMCP.ScreenshotHelper;

public static class ScreenshotHelperHost
{
    public static WebApplication Create(string[] args, ScreenshotSettings? settings = null,
        string? listen = null, string? token = null, bool? allowLan = null)
    {
        settings ??= ScreenshotSettings.FromEnvironment();
        token ??= Environment.GetEnvironmentVariable("FACTORIO_SCREENSHOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("FACTORIO_SCREENSHOT_TOKEN is required (at least 32 non-whitespace characters), including on loopback.");
        listen ??= Environment.GetEnvironmentVariable("FACTORIO_SCREENSHOT_LISTEN") ?? "http://127.0.0.1:8088";
        allowLan ??= Environment.GetEnvironmentVariable("FACTORIO_SCREENSHOT_ALLOW_LAN") == "true";
        if (!Uri.TryCreate(listen, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.AbsolutePath != "/" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
            throw new ArgumentException("FACTORIO_SCREENSHOT_LISTEN must be an HTTP IP address and port, e.g. http://127.0.0.1:8088.");
        if (!IPAddress.IsLoopback(address) && allowLan != true)
            throw new ArgumentException("Non-loopback listening requires FACTORIO_SCREENSHOT_ALLOW_LAN=true.");

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, uri.Port);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = settings.Timeout,
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
        });
        var app = builder.Build();
        app.UseRequestTimeouts();
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes($"Bearer {token}"));
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        // Bound simultaneous reads/decodes as well as each request's bytes and pixel count.
        var gate = new SemaphoreSlim(2);
        app.Lifetime.ApplicationStopped.Register(gate.Dispose);
        app.MapGet("/screenshots/{id}", async (string id, HttpContext context) =>
        {
            if (!ScreenshotFiles.IsCaptureId(id)) return Results.BadRequest();
            await gate.WaitAsync(context.RequestAborted);
            try
            {
                var bytes = await ScreenshotFiles.ReadAsync(settings.Directory, id, settings.MaxBytes, context.RequestAborted);
                ScreenshotFiles.ValidateImage(bytes);
                return Results.File(bytes, "image/png", enableRangeProcessing: false);
            }
            catch (ScreenshotTooLargeException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (InvalidScreenshotException) { return Results.UnprocessableEntity(); }
            catch (FileNotFoundException) { return Results.NotFound(); }
            catch (DirectoryNotFoundException) { return Results.NotFound(); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (IOException) { return Results.UnprocessableEntity(); }
            finally { gate.Release(); }
        });
        return app;
    }
}
