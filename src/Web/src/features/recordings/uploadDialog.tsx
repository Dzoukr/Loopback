"use client";

import { useState } from "react";
import type { WorkflowDto } from "@/lib/generated/api-client";
import { toLocalInput, uploadRecording } from "./upload";

export type UploadItem = {
    key: string;
    file: File;
    /** `datetime-local` value - when the recording was made (the backend has no other source for it). */
    startTime: string;
    state: "pending" | "uploading" | "done" | "error";
    progress: number;
    error?: string;
};

let nextKey = 0;

/** New dialog rows; the recording time defaults to the file's modification time. */
export function toUploadItems(files: File[]): UploadItem[] {
    return files.map((file) => ({
        key: `upload-${nextKey++}`,
        file,
        startTime: toLocalInput(new Date(file.lastModified || Date.now())),
        state: "pending",
        progress: 0,
    }));
}

function formatSize(bytes: number): string {
    return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} kB`;
}

type Props = {
    items: UploadItem[];
    setItems: (update: (items: UploadItem[]) => UploadItem[]) => void;
    workflows: WorkflowDto[];
    /** A recording was added - refresh the list. */
    onUploaded: () => void;
};

/** Upload dialog for dropped / picked audio files; open while it has items. */
export function UploadDialog({ items, setItems, workflows, onUploaded }: Props) {
    // "" = don't process yet (the recording lands in "New").
    const [workflow, setWorkflow] = useState("");
    const [uploading, setUploading] = useState(false);

    const update = (key: string, patch: Partial<UploadItem>) =>
        setItems((all) => all.map((i) => (i.key === key ? { ...i, ...patch } : i)));

    const pending = items.filter((i) => i.state === "pending" || i.state === "error");
    const close = () => setItems(() => []);

    const start = async () => {
        setUploading(true);
        let failed = false;
        // One at a time, so progress is per file and a slow connection is not split.
        for (const item of pending) {
            update(item.key, { state: "uploading", progress: 0, error: undefined });
            try {
                const startTime = new Date(item.startTime);
                await uploadRecording({
                    file: item.file,
                    startTime: Number.isNaN(startTime.getTime()) ? new Date() : startTime,
                    workflow: workflow || null,
                    onProgress: (progress) => update(item.key, { progress }),
                });
                update(item.key, { state: "done", progress: 1 });
                onUploaded();
            } catch (e) {
                failed = true;
                update(item.key, { state: "error", error: e instanceof Error ? e.message : "Upload failed." });
            }
        }
        setUploading(false);
        // Keep the dialog open only when something needs attention.
        if (!failed) setItems((all) => all.filter((i) => i.state !== "done"));
    };

    return (
        <dialog className="modal modal-open" aria-labelledby="upload-title">
            <div className="modal-box max-w-2xl">
                <h3 id="upload-title" className="flex items-center gap-2 text-lg font-semibold">
                    <i className="fa-solid fa-upload text-primary"></i>Upload recordings
                </h3>
                <p className="mt-1 text-sm text-base-content/60">
                    MP3 or OGG, stored as-is and added like a Plaud recording. Set when each one was recorded - it goes into the title and result.
                </p>

                <ul className="mt-4 space-y-2">
                    {items.map((item) => (
                        <li key={item.key} className="rounded-xl border border-base-content/8 bg-base-200/40 p-3">
                            <div className="flex flex-wrap items-center gap-3">
                                <i className={`fa-solid ${
                                    item.state === "done" ? "fa-circle-check text-success"
                                        : item.state === "error" ? "fa-triangle-exclamation text-error"
                                        : "fa-file-audio text-base-content/50"}`}></i>
                                <div className="min-w-0 flex-1">
                                    <div className="truncate text-sm font-medium" title={item.file.name}>{item.file.name}</div>
                                    <div className="text-xs text-base-content/50">{formatSize(item.file.size)}</div>
                                </div>
                                <input
                                    type="datetime-local"
                                    className="input input-sm w-52"
                                    value={item.startTime}
                                    onChange={(e) => update(item.key, { startTime: e.target.value })}
                                    disabled={uploading || item.state === "done"}
                                    aria-label={`Recorded at (${item.file.name})`}
                                />
                                <button
                                    className="btn btn-ghost btn-sm btn-square rounded-lg text-base-content/50"
                                    onClick={() => setItems((all) => all.filter((i) => i.key !== item.key))}
                                    disabled={uploading}
                                    aria-label={`Remove ${item.file.name}`}
                                >
                                    <i className="fa-solid fa-xmark"></i>
                                </button>
                            </div>
                            {item.state === "uploading" && (
                                <div className="mt-2 flex items-center gap-2 text-xs text-base-content/60">
                                    <progress className="progress progress-primary flex-1" value={item.progress < 1 ? item.progress * 100 : undefined} max={100}></progress>
                                    <span className="w-24 text-right">{item.progress < 1 ? `${Math.round(item.progress * 100)} %` : "Saving…"}</span>
                                </div>
                            )}
                            {item.state === "error" && <div className="mt-2 text-xs text-error">{item.error}</div>}
                        </li>
                    ))}
                </ul>

                <div className="modal-action flex-wrap items-center">
                    <select
                        className="select select-sm mr-auto w-56"
                        value={workflow}
                        onChange={(e) => setWorkflow(e.target.value)}
                        disabled={uploading}
                        aria-label="Workflow"
                    >
                        <option value="">Don&apos;t process yet</option>
                        {workflows.map((w) => <option key={w.name} value={w.name}>Process with {w.name}</option>)}
                    </select>
                    <button className="btn btn-sm btn-ghost" onClick={close} disabled={uploading}>
                        {pending.length === 0 ? "Close" : "Cancel"}
                    </button>
                    <button className="btn btn-sm btn-primary" onClick={start} disabled={uploading || pending.length === 0}>
                        {uploading ? <span className="loading loading-spinner loading-xs"></span> : <i className="fa-solid fa-upload"></i>}
                        {workflow ? "Upload & process" : "Upload"}
                    </button>
                </div>
            </div>
            <div className="modal-backdrop bg-base-300/60" onClick={() => { if (!uploading) close(); }}></div>
        </dialog>
    );
}
