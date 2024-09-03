using System.IO.Compression;
using System.Text.Json;
using Microsoft.JSInterop.Streaming;

namespace Microsoft.JSInterop;

public static class JSRuntimeExtensions
{
    public static StreamingDataJsInterop CreateStreamingDataJsInterop(
        this IJSRuntime jsRuntime,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        JsonSerializerOptions? jsonSerializerOptions = null,
        ILogger? logger = null)
    {
        return new(jsRuntime, compressionLevel, jsonSerializerOptions, logger);
    }
}
