# Stream with compression from Blazor Server to JavaScript

In a recent Blazor Server (.NET 8) project I needed to send a large amount of data to Plotly.js (+ 50 MB of data). Blazor already have [support for streaming data from .NET to JavaScript](https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/call-javascript-from-dotnet?view=aspnetcore-8.0#stream-from-net-to-javascript), however, I wanted to also add support for compression of the data that is sent and cancellation of an ongoing stream of data, in case the user changes their mind and wants to view a different set of data, before the current set of data has been sent.

To eanble compression and cancellation, the following is needed:

- Custom JavaScript that can handle decompression and cancellation on the client side of things ([streamingDataJsInterop.js](https://github.com/egil/BlazorServerStreamingToJavaScript/blob/master/wwwroot/streamingDataJsInterop.js)).
- A new .NET type that handles the serialization and compression of data, and cancellation from the server ([StreamingDataJsInterop.cs](https://github.com/egil/BlazorServerStreamingToJavaScript/blob/master/Streaming/StreamingDataJsInterop.cs)).

The `StreamingDataJsInterop` type will allow one active stream of data to be send at the time. Sending new data cancelled any active data streaming before sending the new data. If you want multiple streams of data concurrently, just instantiate as many `StreamingDataJsInterop` as you need.

### Setup

Include the `streamingDataJsInterop.js` in your `wwwroot` and add a script reference in your `App.razor` after `_framework/blazor.web.js`, e.g.:

```html
<script src="_framework/blazor.web.js"></script>
<script src="streamingDataJsInterop.js"></script>
```

Then, add the `StreamingDataJsInterop.cs` to your project, and now you can use it in your components, e.g.:

```razor
@implements IDisposable
@inject IJSRuntime JSRuntime

<!-- ... --> 

@code {
    private StreamingDataJsInterop streamingDataJsInterop = default!;

    protected override void OnInitialized()
    {
        streamingDataJsInterop = new StreamingDataJsInterop(JSRuntime);
    }

    public void Dispose()
    {
        streamingDataJsInterop.Dispose();
    }

    private async Task SendStreamingData()
    {
        var data = // get data from somewhere 
        try
        {
            await streamingDataJsInterop.InvokeStreamingAsync(
                "receiveStreamingData",
                data);
        catch (OperationCanceledException)
        {
            // sending was cancelled...
        }
    }
}
```

Then, add the JavaScript function that should be receive the streamed data, e.g.:

```js
async function receiveStreamingData(streamRef) {
    try {
        const data = await streamRef.getData();
    }
    catch (err) {
        // streaming was cancelled from the server.
    }
}
```

There is a complete sample available on here: [github.com/egil/BlazorServerStreamingToJavaScript](https://github.com/egil/BlazorServerStreamingToJavaScript).

Hope this help!
