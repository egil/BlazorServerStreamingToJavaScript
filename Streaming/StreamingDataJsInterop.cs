using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop.Infrastructure;

namespace Microsoft.JSInterop.Streaming;

public sealed partial class StreamingDataJsInterop : IDisposable
{
    private readonly IJSRuntime jsRuntime;
    private readonly ILogger logger;
    private readonly CompressionLevel compressionLevel;
    private readonly JsonSerializerOptions jsonSerializerOptions;
    private readonly object sendLock = new();
    private CancellationTokenSource cancellationTokenSource;
    private bool streamingActive;
    private Task? currentSendTask;

    /// <summary>
    /// Creates an instance of the <see cref="StreamingDataJsInterop"/> type.
    /// </summary>
    /// <param name="jsRuntime">The <see cref="IJSRuntime"/> to use when streaming.</param>
    /// <param name="compressionLevel">The compression level to use when compression data before streaming.</param>
    /// <param name="jsonSerializerOptions">Custom <see cref="JsonSerializerOptions"/> to use when serializing data to JSON.</param>
    /// <param name="logger"></param>
    public StreamingDataJsInterop(
        IJSRuntime jsRuntime,
        CompressionLevel compressionLevel = CompressionLevel.Optimal,
        JsonSerializerOptions? jsonSerializerOptions = null,
        ILogger? logger = null)
    {
        this.jsRuntime = jsRuntime;
        this.compressionLevel = compressionLevel;
        this.jsonSerializerOptions = jsonSerializerOptions ?? JsonSerializerOptions.Default;
        this.logger = logger ?? NullLogger.Instance;
        cancellationTokenSource = new();
        RegisterAbortJsCallback();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        cancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Invokes the specified JavaScript function asynchronously.
    /// </summary>
    /// <remarks>
    /// If a previous call is still actively streaming data to the JavaScript, that
    /// call is cancelled and this call is then started.
    /// </remarks>
    /// <param name="identifier">An identifier for the function to invoke. For example, the value <c>"someScope.someFunction"</c> will invoke the function <c>window.someScope.someFunction</c>.</param>
    /// <param name="data">The data that should be compressed and serialized to JSON before sending to the client.</param>
    /// <param name="args">JSON-serializable arguments.</param>
    /// <returns>A <see cref="Task"/> that represents the asynchronous invocation operation.</returns>
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Cannot await in lock.")]
    public Task InvokeStreamingVoidAsync<TStreamData>(string identifier, TStreamData data, params object?[]? args)
        => InvokeStreamingAsync<IJSVoidResult, TStreamData>(identifier, data, args);

    /// <summary>
    /// Invokes the specified JavaScript function asynchronously.
    /// </summary>
    /// <remarks>
    /// If a previous call is still actively streaming data to the JavaScript, that
    /// call is cancelled and this call is then started.
    /// </remarks>
    /// <param name="identifier">An identifier for the function to invoke. For example, the value <c>"someScope.someFunction"</c> will invoke the function <c>window.someScope.someFunction</c>.</param>
    /// <param name="data">The data that should be compressed and serialized to JSON before sending to the client.</param>
    /// <param name="args">JSON-serializable arguments.</param>
    /// <returns>A <see cref="Task{TResult}"/> that represents the asynchronous invocation operation.</returns>
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Cannot await in lock.")]
    public Task<TResult> InvokeStreamingAsync<TResult, TStreamData>(string identifier, TStreamData data, params object?[]? args)
    {
        // Only allow one streaming invocation to be active at
        // the same time. If there is one that is active, cancel the
        // previous before proceeding.
        lock (sendLock)
        {
            if (currentSendTask is { IsCompleted: false })
            {
                LogCancellingActiveStream();
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
                cancellationTokenSource = new();
                RegisterAbortJsCallback();
            }

            // If we do not use Task.Run, the full send to the client still
            // completes, even though the task is cancelled correctly.
            // However, if we initiate streaming on a separate thread,
            // the task is cancelled correctly and the client does not receive
            // the data.
            //
            // I.e. replacing this call with:
            //
            // var result = InitiateStreamingAsync<TResult, TStreamData>(identifier, data, cancellationTokenSource.Token, args),
            //
            // will result in the client receiving the data even though the task is cancelled.
            var result = Task.Run(
                () => InitiateStreamingAsync<TResult, TStreamData>(identifier, data, cancellationTokenSource.Token, args),
                cancellationTokenSource.Token);

            currentSendTask = result;

            return result;
        }
    }

    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "Unable to await in cancellation token registration callback.")]
    private void RegisterAbortJsCallback()
    {
        cancellationTokenSource.Token.Register(
            static (state, token) =>
            {
                var @this = (StreamingDataJsInterop)state!;
                if (@this.streamingActive)
                {
                    @this.LogSendingAbortSignal();
                    _ = @this.jsRuntime.InvokeVoidAsync("abortDotNetJsonStream", token.GetHashCode());
                }
            },
            this);
    }

    private async Task<TResult> InitiateStreamingAsync<TResult, TStreamData>(string identifier, TStreamData data, CancellationToken cancellationToken, params object?[]? args)
    {
        try
        {
            LogComressingData(identifier);
            var stream = await CreateCompressedJsonStream(data, cancellationToken);

            // Setting streamingActive signals that an abort signal
            // should be sent to JavaScript from the cancellation token
            // registration.
            // Having the boolean toggle avoids unnecessary abort calls
            // to JavaScrip if streaming was cancelled during compression.
            streamingActive = true;

            LogStartStreaming(identifier, stream.Length);
            var streamRef = new DotNetGZippedJsonStreamReference
            {
                Ref = new DotNetStreamReference(stream),
                StreamId = cancellationToken.GetHashCode(),
            };

            var result = await jsRuntime.InvokeAsync<TResult>(
                identifier,
                cancellationToken,
                args is { Length: > 0 } ? [streamRef, .. args] : [streamRef]);

            LogFinishedStreaming(identifier);

            return result;
        }
        catch (OperationCanceledException ex)
        {
            LogCancelledStreaming(ex, identifier);
            throw;
        }
        finally
        {
            streamingActive = false;
        }
    }

    private async Task<MemoryStream> CreateCompressedJsonStream<TData>(TData data, CancellationToken cancellationToken)
    {
        // Stream is closed by DotNetStreamReference/JSInterop when its done sending or streaming gets cancelled.
        // Thus, zipStream should leave it open.
        var resultStream = new MemoryStream();

        await using (var zipStream = new GZipStream(resultStream, compressionLevel, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(zipStream, data, jsonSerializerOptions, cancellationToken: cancellationToken);
        }

        resultStream.Position = 0;
        return resultStream;
    }

    private sealed class DotNetGZippedJsonStreamReference
    {
        [JsonPropertyName(name: "__dotNetGZippedJsonStream")]
        public required int StreamId { get; init; }

        [JsonPropertyName(name: "ref")]
        public required DotNetStreamReference Ref { get; init; }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Compressing data for {Identifier}.")]
    private partial void LogComressingData(string identifier);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Start streaming {Size} data to {Identifier}.")]
    private partial void LogStartStreaming(string identifier, long size);

    [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Finished streaming data to {Identifier}.")]
    private partial void LogFinishedStreaming(string identifier);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Cancelling active stream.")]
    private partial void LogCancellingActiveStream();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Sending abort signal to JavaScript.")]
    private partial void LogSendingAbortSignal();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Cancelled streaming data to {Identifier}.")]
    private partial void LogCancelledStreaming(OperationCanceledException exception, string identifier);
}