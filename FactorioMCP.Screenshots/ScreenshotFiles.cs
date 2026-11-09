using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Memory;

namespace FactorioMCP.Screenshots;

public sealed class ScreenshotUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ScreenshotTooLargeException(string message) : Exception(message);
public sealed class InvalidScreenshotException(string message, Exception? inner = null) : Exception(message, inner);

public static class ScreenshotFiles
{
    public static bool IsCaptureId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string RelativePath(string id)
    {
        if (!IsCaptureId(id)) throw new ArgumentException("Invalid screenshot capture ID.", nameof(id));
        return $"ai-player/vision-{id}.png";
    }

    public static async Task<byte[]> ReadAsync(string directory, string id, int maxBytes, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        var path = Path.Combine(root, RelativePath(id));
        // No request-supplied path is accepted. Also refuse symlinks/junctions in the served subtree.
        foreach (var entry in new[] { root, Path.Combine(root, "ai-player"), path })
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Screenshot symlinks/junctions are not permitted.");
        }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedAsync(file, file.Length, maxBytes, ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, long? length, int maxBytes, CancellationToken ct)
    {
        if (length > maxBytes) throw new ScreenshotTooLargeException($"Screenshot exceeds {maxBytes} bytes.");
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maxBytes) throw new ScreenshotTooLargeException($"Screenshot exceeds {maxBytes} bytes.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    public static DecoderOptions DecodeOptions { get; } = new()
    {
        SkipMetadata = true,
        MaxFrames = 1,
        Configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule())
        {
            MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
            {
                AllocationLimitMegabytes = 128,
                MaximumPoolSizeMegabytes = 32
            })
        }
    };

    public static void ValidateImage(byte[] bytes)
    {
        // Reject non-PNG, huge dimensions and incomplete writes before invoking any decoder.
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        ReadOnlySpan<byte> iend = [0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
        if (bytes.Length < 45 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidScreenshotException("Screenshot contains invalid PNG data.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width > ScreenshotSettings.MaxDimension || height > ScreenshotSettings.MaxDimension)
            throw new ScreenshotTooLargeException("Screenshot pixel dimensions exceed the 4096x4096 limit.");
        if (!bytes.AsSpan(bytes.Length - 12).SequenceEqual(iend))
            throw new InvalidScreenshotException("Screenshot PNG is incomplete (missing IEND).");
        try
        {
            var info = Image.Identify(DecodeOptions, bytes);
            if (info.Metadata.DecodedImageFormat?.Name != "PNG")
                throw new InvalidScreenshotException("Screenshot is not a PNG image.");
            if (info.Width > ScreenshotSettings.MaxDimension || info.Height > ScreenshotSettings.MaxDimension ||
                (long)info.Width * info.Height > ScreenshotSettings.MaxPixels)
                throw new ScreenshotTooLargeException("Screenshot pixel dimensions exceed the 4096x4096 limit.");
            // Identify alone does not reject truncated/corrupt pixel data.
            using var decoded = Image.Load(DecodeOptions, bytes);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or InvalidMemoryOperationException)
        {
            throw new InvalidScreenshotException("Screenshot contains invalid or incomplete image data.", ex);
        }
    }
}
