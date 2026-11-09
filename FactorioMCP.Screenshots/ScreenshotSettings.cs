using System.Globalization;

namespace FactorioMCP.Screenshots;

public sealed record ScreenshotSettings
{
    public string Transport { get; init; } = "local";
    public string Directory { get; init; } = ResolveDirectory();
    public Uri? Endpoint { get; init; }
    public string? Token { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxBytes { get; init; } = 32 * 1024 * 1024;
    public const int MaxDimension = 4096;
    public const long MaxPixels = 16_777_216;

    // Explicit OS inputs also allow testing both defaults on either operating system.
    public static string ResolveDirectory(string? custom = null, bool? windows = null,
        string? home = null, string? appData = null)
    {
        if (!string.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);
        return (windows ?? OperatingSystem.IsWindows())
            ? Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Factorio", "script-output")
            : Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".factorio", "script-output");
    }

    public static ScreenshotSettings FromEnvironment()
    {
        var transport = Environment.GetEnvironmentVariable("FACTORIO_SCREENSHOT_TRANSPORT") ?? "local";
        if (transport is not ("local" or "http"))
            throw new ArgumentException("FACTORIO_SCREENSHOT_TRANSPORT must be local or http.");
        var endpoint = Environment.GetEnvironmentVariable("FACTORIO_SCREENSHOT_ENDPOINT");
        var settings = new ScreenshotSettings
        {
            Transport = transport,
            Directory = ResolveDirectory(Environment.GetEnvironmentVariable("FACTORIO_SCRIPT_OUTPUT_DIR")),
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : new Uri(endpoint, UriKind.Absolute),
            Token = Environment.GetEnvironmentVariable("FACTORIO_SCREENSHOT_TOKEN"),
            Timeout = TimeSpan.FromSeconds(ReadInt("FACTORIO_SCREENSHOT_TIMEOUT_SECONDS", 15, 1, 120)),
            MaxBytes = ReadInt("FACTORIO_SCREENSHOT_MAX_BYTES", 32 * 1024 * 1024, 1024, 64 * 1024 * 1024)
        };
        if (transport == "http") settings.ValidateRemote();
        return settings;
    }

    public void ValidateRemote()
    {
        if (Endpoint is null || Endpoint.Scheme is not ("http" or "https") ||
            Endpoint.AbsolutePath != "/" || Endpoint.Query != "" || Endpoint.Fragment != "" || Endpoint.UserInfo != "")
            throw new ArgumentException("FACTORIO_SCREENSHOT_ENDPOINT must be an HTTP(S) origin, e.g. http://192.168.1.20:8088.");
        if (string.IsNullOrWhiteSpace(Token) || Token.Length < 32 || Token.Any(char.IsWhiteSpace))
            throw new ArgumentException("FACTORIO_SCREENSHOT_TOKEN must contain at least 32 non-whitespace characters.");
    }

    public static int ReadInt(string name, int fallback, int min, int max)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text is null) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new ArgumentException($"{name} must be between {min} and {max}.");
        return value;
    }
}
