export function formatDuration(ms: number): string {
    const totalMinutes = Math.round(ms / 60000);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    return hours > 0 ? `${hours} h ${minutes} min` : `${minutes} min`;
}

export function formatDateTime(iso: string | null | undefined): string {
    if (!iso) return "—";
    return new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

export type RecordingGroup = "new" | "inProgress" | "failed" | "processed";

export function groupOf(status: string): RecordingGroup {
    switch (status) {
        case "synced": return "new";
        case "failed": return "failed";
        case "done": return "processed";
        default: return "inProgress";
    }
}

/** Seconds as m:ss or h:mm:ss (player time labels). */
export function formatClock(seconds: number): string {
    const s = Math.max(0, Math.floor(Number.isFinite(seconds) ? seconds : 0));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = String(s % 60).padStart(2, "0");
    return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${sec}` : `${m}:${sec}`;
}

/** Recording name usable as a file name (no extension, no characters Windows forbids). */
export function safeFileName(name: string): string {
    return name.replace(/\.[^.]+$/, "").replace(/[\\/:*?"<>|]+/g, "_").trim() || "recording";
}

/** Saves text generated in the browser as a file. */
export function downloadText(fileName: string, content: string, type: string) {
    const url = URL.createObjectURL(new Blob([content], { type }));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
}
