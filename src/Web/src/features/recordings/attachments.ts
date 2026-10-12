// Attachment uploads go to the route handler (app/api/recordings/[id]/attachments), not a server
// action: the body is binary and may be large, and XHR reports upload progress (fetch does not).
import type { AttachmentDto } from "@/lib/generated/api-client";

/** Same limit as the backend (AttachmentStore.maxFileBytes) - checked here so a too large file fails right away. */
export const maxAttachmentBytes = 50 * 1024 * 1024;

export type AttachArgs = {
    recordingId: string;
    file: File;
    /** The file's last-modified time; null for clipboard pastes (it is the paste time there). */
    lastModified: number | null;
    onProgress: (fraction: number) => void;
};

/** Resolves with whether the same file was attached already. */
export function uploadAttachment({ recordingId, file, lastModified, onProgress }: AttachArgs): Promise<{ alreadyExisted: boolean }> {
    if (file.size > maxAttachmentBytes) return Promise.reject(new Error("The file is larger than 50 MB"));
    const form = new FormData();
    form.append("file", file);
    if (lastModified) form.append("lastModified", String(lastModified));

    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open("POST", `/api/recordings/${encodeURIComponent(recordingId)}/attachments`);
        xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(e.loaded / e.total); };
        xhr.onload = () => {
            if (xhr.status >= 200 && xhr.status < 300) {
                try {
                    resolve({ alreadyExisted: (JSON.parse(xhr.responseText) as { alreadyExisted: boolean }).alreadyExisted });
                } catch {
                    reject(new Error("Unexpected response from the server."));
                }
            } else {
                reject(new Error((xhr.status === 400 || xhr.status === 404) && xhr.responseText ? xhr.responseText : `Upload failed (HTTP ${xhr.status}).`));
            }
        };
        xhr.onerror = () => reject(new Error("The Loopback server is not reachable."));
        xhr.send(form);
    });
}

/**
 * Files from a paste: screenshots pasted from the clipboard carry the paste time as lastModified, which
 * says nothing about when they were captured - such times (within a few seconds of now) are dropped.
 */
export function pastedLastModified(file: File): number | null {
    return Math.abs(Date.now() - file.lastModified) < 10_000 ? null : file.lastModified;
}

export function attachmentUrl(recordingId: string, attachmentId: string, download = false): string {
    return `/api/recordings/${encodeURIComponent(recordingId)}/attachments/${encodeURIComponent(attachmentId)}${download ? "?download=1" : ""}`;
}

/** Mark kinds: a device button press, a note typed in Plaud, a voice memo (its note is the transcript). */
export const markKinds: Record<string, { icon: string; label: string }> = {
    highlight: { icon: "fa-flag", label: "Highlight" },
    note: { icon: "fa-note-sticky", label: "Note" },
    voice: { icon: "fa-microphone", label: "Voice memo" },
};

export function markKindOf(kind: string): { icon: string; label: string } {
    return markKinds[kind] ?? markKinds.highlight;
}

export const kindIcons: Record<string, string> = {
    image: "fa-file-image",
    pdf: "fa-file-pdf",
    text: "fa-file-lines",
    other: "fa-file",
};

export function formatSize(bytes: number): string {
    return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} kB`;
}

/** Offset as hh:mm:ss (always with hours, the format the time input expects). */
export function formatOffset(seconds: number): string {
    const s = Math.max(0, Math.round(seconds));
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${pad(Math.floor(s / 3600))}:${pad(Math.floor((s % 3600) / 60))}:${pad(s % 60)}`;
}

/** "1:02:03", "02:03", "123" -> seconds; null when it is not a time. */
export function parseOffset(text: string): number | null {
    const parts = text.trim().split(":");
    if (parts.length > 3 || parts.some((p) => !/^\d{1,3}$/.test(p))) return null;
    return parts.reduce((total, p) => total * 60 + Number(p), 0);
}

export function isDuringCall(a: AttachmentDto): boolean {
    return a.placement === "duringCall" && a.offsetSeconds !== null;
}

/** Placement badge text: the time in the call, or why it is a reference. */
export function placementLabel(a: AttachmentDto, formatClock: (seconds: number) => string): string {
    if (isDuringCall(a)) return formatClock(a.offsetSeconds ?? 0);
    return a.timeSource === "none" ? "Reference - time unknown" : "Reference";
}
