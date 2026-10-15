// Sends a chosen, dropped or pasted file to one of the app's upload endpoints. The browser keeps the
// File; C# only learns its name, size and a local preview address, and asks for the upload by id.
const files = new Map();
let counter = 0;

export function watch(inputs, zone, dotnet) {
    const take = (file) => {
        if (!file) return;
        files.clear();
        const id = String(++counter);
        files.set(id, file);
        const preview = file.type.startsWith('image/') ? URL.createObjectURL(file) : null;
        dotnet.invokeMethodAsync('OnFileChosen', id, file.name, file.size, preview);
    };

    const onChange = (e) => { take(e.target.files[0]); e.target.value = ''; };
    for (const input of inputs) input.addEventListener('change', onChange);

    const onOver = (e) => { e.preventDefault(); };
    const onDrop = (e) => { e.preventDefault(); take(e.dataTransfer?.files?.[0]); };
    const onPaste = (e) => {
        const item = [...(e.clipboardData?.items ?? [])].find((i) => i.type.startsWith('image/'));
        if (item) take(item.getAsFile());
    };
    if (zone) {
        zone.addEventListener('dragover', onOver);
        zone.addEventListener('drop', onDrop);
        document.addEventListener('paste', onPaste);
    }

    return {
        dispose() {
            for (const input of inputs) input.removeEventListener('change', onChange);
            if (zone) {
                zone.removeEventListener('dragover', onOver);
                zone.removeEventListener('drop', onDrop);
                document.removeEventListener('paste', onPaste);
            }
        },
    };
}

export async function send(id, url, contentType) {
    const file = files.get(id);
    if (!file) return { status: 0, body: '' };
    try {
        const response = await fetch(url, {
            method: 'POST',
            body: file,
            credentials: 'same-origin',
            headers: { 'Content-Type': contentType || (file.type.startsWith('image/') ? file.type : 'image/jpeg') },
        });
        return { status: response.status, body: await response.text() };
    } catch {
        return { status: 0, body: '' };
    }
}

export function release(id, previewUrl) {
    files.delete(id);
    if (previewUrl) URL.revokeObjectURL(previewUrl);
}
