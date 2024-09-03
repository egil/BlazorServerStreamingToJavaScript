const dotNetGZippedJsonStream = "__dotNetGZippedJsonStream";
const abortControllers = {};

DotNet.attachReviver((key, value) => {
    if (value && typeof value === "object" && value.hasOwnProperty(dotNetGZippedJsonStream)) {
        const streamId = value[dotNetGZippedJsonStream];
        const ref = value["ref"];
        return new DotNetGZippedJsonStream(ref, streamId, abortControllers);
    }

    return value;
});

function abortDotNetJsonStream(streamId) {
    if (abortControllers[streamId]) {
        abortControllers[streamId].abort();
    }
}

class DotNetGZippedJsonStream {
    constructor(_streamRef, _streamId, _abortControllers) {
        this._streamRef = _streamRef;
        this._streamId = _streamId;
        this._abortControllers = _abortControllers;
    }

    async getData() {
        const controller = new AbortController();
        this._abortControllers[this._streamId] = controller;
        try {
            const stream = await this._streamRef.stream();
            const decompressedStream = stream.pipeThrough(
                new DecompressionStream('gzip'),
                { signal: controller.signal });
            const json = await new Response(decompressedStream).json();
            return json;
        } catch (err) {
            if (controller.signal.aborted)
                throw new Error('GZipped JSON stream was aborted from .NET');
            else
                throw err;
        }
        finally {
            delete this._abortControllers[this._streamId];
        }
    }
}