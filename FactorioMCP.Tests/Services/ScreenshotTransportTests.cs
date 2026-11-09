using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using FactorioMCP.Rcon;
using FactorioMCP.ScreenshotHelper;
using FactorioMCP.Screenshots;
using FactorioMCP.Services;
using FactorioMCP.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using ModelContextProtocol.Protocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace FactorioMCP.Tests.Services;

// Real Kestrel HTTP server, real temporary files, no game/save dependency.
public sealed class ScreenshotTransportTests : IAsyncLifetime
{
    private const string Token = "test-only-screenshot-token-0123456789abcdef";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "factorio-screenshot-tests", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private Uri _endpoint = null!;
    private static string NewId() => Guid.NewGuid().ToString("N");
    private ScreenshotSettings Settings(bool remote) => new()
    {
        Transport = remote ? "http" : "local", Directory = _directory, Endpoint = _endpoint,
        Token = Token, Timeout = TimeSpan.FromMilliseconds(500), MaxBytes = 1024 * 1024
    };

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "ai-player"));
        _app = ScreenshotHelperHost.Create([], new ScreenshotSettings { Directory = _directory, MaxBytes = 1024 * 1024 },
            "http://127.0.0.1:0", Token);
        await _app.StartAsync();
        _endpoint = new Uri(_app.Urls.Single());
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    private static byte[] Png(int width = 16, int height = 16)
    {
        using var image = new Image<Rgba32>(width, height);
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private Task Write(string id, byte[] bytes) => File.WriteAllBytesAsync(Path.Combine(_directory, ScreenshotFiles.RelativePath(id)), bytes);

    [Fact]
    public void PathDefaultsAreIndependentOfCurrentHostOS()
    {
        Assert.Equal(Path.Combine("roaming", "Factorio", "script-output"), ScreenshotSettings.ResolveDirectory(windows: true, appData: "roaming"));
        Assert.Equal(Path.Combine("home", ".factorio", "script-output"), ScreenshotSettings.ResolveDirectory(windows: false, home: "home"));
        Assert.Equal(Path.GetFullPath(_directory), ScreenshotSettings.ResolveDirectory(_directory, windows: true));
        Assert.Equal(Path.GetFullPath(_directory), ScreenshotSettings.ResolveDirectory(_directory, windows: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrievesRequestedImage(bool remote)
    {
        var id = NewId(); var png = Png();
        await Write(id, png);
        using var retriever = new ScreenshotRetriever(Settings(remote));
        Assert.Equal(png, await retriever.RetrieveAsync(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitsForDelayedFileAndRetriesIncompleteWrites(bool remote)
    {
        var id = NewId(); var png = Png();
        using var retriever = new ScreenshotRetriever(Settings(remote) with { Timeout = TimeSpan.FromSeconds(3) });
        var pending = retriever.RetrieveAsync(id);
        await Task.Delay(100);
        await Write(id, [1, 2, 3]);
        await Task.Delay(100);
        await Write(id, png);
        Assert.Equal(png, await pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCaptureNeverReturnsPreviousImage(bool remote)
    {
        var first = NewId(); var second = NewId();
        await Write(first, Png());
        await File.WriteAllBytesAsync(Path.Combine(_directory, "ai-player", "vision.png"), Png());
        using var retriever = new ScreenshotRetriever(Settings(remote));
        Assert.NotEmpty(await retriever.RetrieveAsync(first));
        var error = await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(second));
        Assert.Contains(second, error.Message);
        Assert.Contains("timed out", error.Message);
        Assert.Contains("headless", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidImageIsExplicitFailure(bool remote)
    {
        var id = NewId(); await Write(id, [0, 1, 2, 3]);
        using var retriever = new ScreenshotRetriever(Settings(remote));
        var error = await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(id));
        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcesByteAndPixelLimits(bool remote)
    {
        var id = NewId(); await Write(id, new byte[1024 * 1024 + 1]);
        using var retriever = new ScreenshotRetriever(Settings(remote));
        var error = await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(id));
        Assert.IsType<ScreenshotTooLargeException>(error.InnerException);
        await Write(id, Png(4097, 1));
        error = await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(id));
        Assert.IsType<ScreenshotTooLargeException>(error.InnerException);
    }

    [Fact]
    public async Task RejectsWrongOrMissingAuthentication()
    {
        var id = NewId(); await Write(id, Png());
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(_endpoint, $"/screenshots/{id}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var retriever = new ScreenshotRetriever(Settings(true) with { Token = new string('x', 32) });
        var error = await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(id));
        Assert.Contains("authentication failed", error.Message);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("C:\\secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("vision.png")]
    public async Task RejectsPathsInsteadOfCaptureIds(string path)
    {
        using var retriever = new ScreenshotRetriever(Settings(false));
        await Assert.ThrowsAsync<ArgumentException>(() => retriever.RetrieveAsync(path));
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await client.GetAsync(new Uri(_endpoint, "/screenshots/" + Uri.EscapeDataString(path)));
        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public void HelperRefusesLanWithoutOptInAndRefusesMissingToken()
    {
        Assert.Throws<ArgumentException>(() => ScreenshotHelperHost.Create([], Settings(false), "http://0.0.0.0:8088", Token, false));
        Assert.Throws<ArgumentException>(() => ScreenshotHelperHost.Create([], Settings(false), token: ""));
    }

    [Fact]
    public async Task ReadFailureIsDiagnosed()
    {
        var id = NewId(); await Write(id, Png());
        using var locked = new FileStream(Path.Combine(_directory, ScreenshotFiles.RelativePath(id)), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var retriever = new ScreenshotRetriever(Settings(false));
        await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(id));
    }

    [Fact]
    public async Task NetworkFailureAfterCleanShutdownIsDiagnosed()
    {
        await _app.StopAsync();
        using var retriever = new ScreenshotRetriever(Settings(true) with { Timeout = TimeSpan.FromSeconds(10) });
        var error = await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(NewId()));
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task SlowHttpResponseTimesOut()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var slow = builder.Build();
        slow.MapGet("/screenshots/{id}", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
            await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted));
        await slow.StartAsync();
        using var retriever = new ScreenshotRetriever(Settings(true) with { Endpoint = new Uri(slow.Urls.Single()) });
        var error = await Assert.ThrowsAsync<ScreenshotUnavailableException>(() => retriever.RetrieveAsync(NewId()));
        Assert.Contains("timed out", error.Message);
        await slow.StopAsync();
    }

    [Fact]
    public async Task CallerCancellationIsNotMisreportedAsMissingImage()
    {
        using var retriever = new ScreenshotRetriever(Settings(false));
        using var ct = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retriever.RetrieveAsync(NewId(), ct.Token));
    }

    private sealed class ScreenshotRcon(Func<string, Task<string>> execute) : RconClient
    {
        public override Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default) => execute(command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpToolReturnsActualImageAndLegendAndSerializesRepeatedCaptures(bool remote)
    {
        const string legend = "{\"entity_count\":1,\"entities\":[{\"id\":1,\"name\":\"stone-furnace\"}]}";
        var ids = new List<string>();
        using var rcon = new ScreenshotRcon(async command =>
        {
            var id = Regex.Match(command, @"vision-([0-9a-f]{32})\.png").Groups[1].Value;
            ids.Add(id);
            await Write(id, Png());
            return legend;
        });
        using var vision = new VisionService(rcon, Settings(remote));
        using var queue = new GameCommandQueue();
        var tool = new VisionTools(vision, queue);
        var results = await Task.WhenAll(tool.TakeScreenshot(), tool.TakeScreenshot());
        Assert.Equal(2, ids.Distinct().Count());
        foreach (var result in results)
        {
            var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/jpeg", image.MimeType);
            using var decoded = Image.Load(image.DecodedData.Span);
            Assert.Equal(16, decoded.Width);
            Assert.Contains(legend, Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        }
    }

    [Fact]
    public async Task McpToolPreservesLegendWithDiagnosticWhenImageUnavailable()
    {
        const string legend = "{\"entity_count\":0,\"entities\":[]}";
        using var rcon = new ScreenshotRcon(_ => Task.FromResult(legend));
        using var vision = new VisionService(rcon, Settings(false));
        using var queue = new GameCommandQueue();
        var result = await new VisionTools(vision, queue).TakeScreenshot();
        Assert.Empty(result.Content.OfType<ImageContentBlock>());
        Assert.Contains(result.Content.OfType<TextContentBlock>(), b => b.Text.Contains(legend));
        Assert.Contains(result.Content.OfType<TextContentBlock>(), b => b.Text.Contains("Image unavailable:"));
    }

    [Fact]
    public async Task FactorioCaptureErrorDoesNotWaitForNonexistentFile()
    {
        const string legend = "{\"entity_count\":0,\"entities\":[],\"screenshot_error\":\"No connected screenshot player\"}";
        using var rcon = new ScreenshotRcon(_ => Task.FromResult(legend));
        using var vision = new VisionService(rcon, Settings(false));
        var capture = await vision.CaptureAsync(null, null, 1, 1920, 1080, default);
        Assert.Null(capture.Image);
        Assert.Equal("No connected screenshot player", capture.Diagnostic);
        Assert.Equal(legend, capture.Legend);
    }
}
