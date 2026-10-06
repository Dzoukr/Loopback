// Uploads go to the route handler (app/api/recordings/upload), not a server action: the body is
// large and binary, and XHR reports upload progress (fetch does not).

/** Formats the backend decodes: MP3 and Ogg (Vorbis / Opus). It checks the content, this only filters drops. */
export const acceptedExtensions = [".mp3", ".ogg", ".oga", ".opus"];

const acceptedTypes = ["audio/mpeg", "audio/mp3", "audio/ogg", "audio/opus"];

export const acceptAttribute = [...acceptedTypes, ...acceptedExtensions].join(",");

export function isAcceptedAudio(file: File): boolean {
    const name = file.name.toLowerCase();
    return acceptedTypes.includes(file.type) || acceptedExtensions.some((ext) => name.endsWith(ext));
}

export type UploadArgs = {
    file: File;
    startTime: Date;
    /** Queue for processing right away; null = lands in "New". */
    workflow: string | null;
    onProgress: (fraction: number) => void;
};

export function uploadRecording({ file, startTime, workflow, onProgress }: UploadArgs): Promise<string> {
    const form = new FormData();
    form.append("file", file);
    form.append("startTime", String(startTime.getTime()));
    // The recording's local offset (DST of that date), e.g. 120 for UTC+02:00.
    form.append("utcOffsetMinutes", String(-startTime.getTimezoneOffset()));
    if (workflow) form.append("workflow", workflow);

    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open("POST", "/api/recordings/upload");
        xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(e.loaded / e.total); };
        xhr.onload = () => {
            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    resolve((JSON.parse(xhr.responseText) as { recordingId: string }).recordingId);
                } catch {
                    reject(new Error("Unexpected response from the server."));
                }
            } else {
                reject(new Error(xhr.status === 400 && xhr.responseText ? xhr.responseText : `Upload failed (HTTP ${xhr.status}).`));
            }
        };
        xhr.onerror = () => reject(new Error("The Loopback server is not reachable."));
        xhr.send(form);
    });
}

/** Date as the value of an `<input type="datetime-local">` (local time, minutes). */
export function toLocalInput(date: Date): string {
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
