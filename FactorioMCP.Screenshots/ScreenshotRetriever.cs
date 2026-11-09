using System.Net;
using System.Net.Http.Headers;

namespace FactorioMCP.Screenshots;

/// <summary>Retrieves only the unique file for this capture; never falls back to a previous image.</summary>
public sealed class ScreenshotRetriever : IDisposable
{
    private readonly ScreenshotSettings _settings;
    private readonly HttpClient _http;

    public ScreenshotRetriever(ScreenshotSettings settings)
    {
        _settings = settings;
        if (settings.Transport == "http") settings.ValidateRemote();
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
    }

    public async Task<byte[]> RetrieveAsync(string captureId, CancellationToken ct = default)
    {
        _ = ScreenshotFiles.RelativePath(captureId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_settings.Timeout);
        string lastIssue = "Screenshot file not found.";
        try
        {
            while (true)
            {
                try
                {
                    var bytes = _settings.Transport == "http"
                        ? await ReadRemoteAsync(captureId, deadline.Token).ConfigureAwait(false)
                        : await ScreenshotFiles.ReadAsync(_settings.Directory, captureId, _settings.MaxBytes, deadline.Token).ConfigureAwait(false);
                    ScreenshotFiles.ValidateImage(bytes);
                    return bytes;
                }
                catch (Exception ex) when (ex is IOException or InvalidScreenshotException)
                {
                    // Factorio may not have created/finished writing the file yet.
                    lastIssue = ex is FileNotFoundException or DirectoryNotFoundException ? "Screenshot file not found." : ex.Message;
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ScreenshotUnavailableException($"Screenshot {captureId} timed out after {_settings.Timeout.TotalSeconds:g}s. {lastIssue} Check the graphical Factorio instance (headless cannot capture), selected player, script-output directory, and helper endpoint.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or ScreenshotTooLargeException)
        {
            throw new ScreenshotUnavailableException($"Screenshot {captureId} unavailable: {ex.Message}", ex);
        }
    }

    private async Task<byte[]> ReadRemoteAsync(string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_settings.Endpoint!, $"/screenshots/{id}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException();
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity) throw new InvalidScreenshotException("Helper reports an incomplete or invalid screenshot.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Screenshot helper authentication failed (check FACTORIO_SCREENSHOT_TOKEN).");
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new ScreenshotTooLargeException("Screenshot exceeds helper size limits.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Screenshot helper returned HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ScreenshotFiles.ReadBoundedAsync(stream, response.Content.Headers.ContentLength, _settings.MaxBytes, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
