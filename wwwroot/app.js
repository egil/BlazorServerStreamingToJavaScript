/**
 * 
 * @param {DotNetGZippedJsonStream} streamRef
 * @param {HTMLElement} elmRef
 */
async function receiveNamesList(streamRef, elmRef) {
    try {
        const list = await streamRef.getData();
        const li = document.createElement("li");
        li.innerHTML = list.length + ' names received at client.';
        elmRef.prepend(li);
        return list.length;
    }
    catch (err) {
        const li = document.createElement("li");
        li.innerHTML = err;
        elmRef.prepend(li);
    }
}