"use client";

import { useEffect, useRef, useState } from "react";
import type { DragEvent } from "react";
import type { AttachmentDto, WorkflowDto, RecordingDto } from "@/lib/generated/api-client";
import { deleteRecording, processRecording, reprocessRecording, retryRecording } from "./actions";
import { attachmentUrl, isDuringCall, markKindOf, pastedLastModified, uploadAttachment } from "./attachments";
import { AttachmentsView, Lightbox, MarksList, openAttachment, type UploadTile } from "./attachmentsView";
import { AudioPlayer, type PlayerHandle } from "./audioPlayer";
import { ResultView } from "./resultView";
import { TranscriptView } from "./transcriptView";
import { formatDateTime, formatDuration } from "./utils";

const steps = ["queued", "transcribing", "summarizing", "done"];

const statusStyles: Record<string, { badge: string; icon: string; tile: string }> = {
    synced: { badge: "badge-primary", icon: "fa-microphone-lines", tile: "bg-primary/12 text-primary" },
    queued: { badge: "badge-info", icon: "fa-hourglass-start", tile: "bg-info/12 text-info" },
    transcribing: { badge: "badge-info", icon: "fa-wave-square", tile: "bg-info/12 text-info" },
    summarizing: { badge: "badge-info", icon: "fa-wand-magic-sparkles", tile: "bg-info/12 text-info" },
    done: { badge: "badge-success", icon: "fa-file-lines", tile: "bg-success/12 text-success" },
    failed: { badge: "badge-error", icon: "fa-triangle-exclamation", tile: "bg-error/12 text-error" },
};

const stepLabels: Record<string, string> = {
    synced: "New",
    queued: "Queued",
    transcribing: "Transcribing",
    summarizing: "Summarizing",
    done: "Done",
    failed: "Failed",
};

type Props = {
    recording: RecordingDto;
    workflows: WorkflowDto[];
    /** The card Ctrl+V attaches to (last clicked / focused). */
    active: boolean;
    onActivate: () => void;
    /** Files are being dragged over the page - every card shows it is a drop target. */
    dragging: boolean;
    onChanged: () => void;
    onError: (message: string) => void;
};

/** Parallel attachment uploads per card. */
const uploadConcurrency = 3;
let nextUploadKey = 0;

export function RecordingCard({ recording, workflows, active, onActivate, dragging, onChanged, onError }: Props) {
    const [chosenWorkflow, setWorkflow] = useState<string | null>(recording.workflow ?? null);
    // Workflows load after the first render, so fall back to the first one until the user picks.
    const workflow = chosenWorkflow ?? workflows[0]?.name ?? "";
    const [busy, setBusy] = useState(false);
    const [transcriptOpened, setTranscriptOpened] = useState(false);

    const attachments = recording.attachments ?? [];
    const marks = recording.marks ?? [];
    const [uploads, setUploads] = useState<UploadTile[]>([]);
    const [attachmentsOpen, setAttachmentsOpen] = useState(false);
    const [dropOver, setDropOver] = useState(false);
    const [previewing, setPreviewing] = useState<AttachmentDto | null>(null);
    const [highlightedId, setHighlightedId] = useState<string | null>(null);
    const playerRef = useRef<PlayerHandle>(null);
    const fileInput = useRef<HTMLInputElement>(null);

    const updateUpload = (key: string, patch: Partial<UploadTile>) =>
        setUploads((all) => all.map((u) => (u.key === key ? { ...u, ...patch } : u)));
    const dismissUpload = (key: string) => setUploads((all) => all.filter((u) => u.key !== key));

    /** Uploads right away, no dialog; the time is detected by the backend and editable afterwards. */
    const attachFiles = async (files: File[], pasted: boolean) => {
        if (files.length === 0) return;
        setAttachmentsOpen(true);
        const queue = files.map((file) => ({ file, key: `attach-${nextUploadKey++}` }));
        setUploads((all) => [...all, ...queue.map(({ file, key }) => ({ key, name: file.name, progress: 0, state: "uploading" as const }))]);
        const worker = async () => {
            for (let next = queue.shift(); next; next = queue.shift()) {
                const { file, key } = next;
                try {
                    const { alreadyExisted } = await uploadAttachment({
                        recordingId: recording.id,
                        file,
                        lastModified: pasted ? pastedLastModified(file) : file.lastModified,
                        onProgress: (progress) => updateUpload(key, { progress }),
                    });
                    if (alreadyExisted) updateUpload(key, { state: "error", error: "Already attached" });
                    else dismissUpload(key);
                    onChanged();
                } catch (e) {
                    updateUpload(key, { state: "error", error: e instanceof Error ? e.message : "Upload failed." });
                }
            }
        };
        await Promise.all(Array.from({ length: Math.min(uploadConcurrency, queue.length) }, worker));
    };

    // Ctrl+V attaches clipboard files / screenshots to the active card (not while typing into a field).
    useEffect(() => {
        if (!active) return;
        const onPaste = (e: ClipboardEvent) => {
            const target = e.target as HTMLElement | null;
            if (target?.closest("input, textarea, [contenteditable=true]")) return;
            const files = Array.from(e.clipboardData?.files ?? []);
            if (files.length === 0) return;
            e.preventDefault();
            attachFiles(files, true);
        };
        window.addEventListener("paste", onPaste);
        return () => window.removeEventListener("paste", onPaste);
    });

    const hasFiles = (e: DragEvent) => e.dataTransfer.types.includes("Files");
    const onDragOver = (e: DragEvent<HTMLDivElement>) => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = "copy";
        setDropOver(true);
    };
    const onDragLeave = (e: DragEvent<HTMLDivElement>) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDropOver(false);
    };
    const onDrop = (e: DragEvent<HTMLDivElement>) => {
        if (!hasFiles(e)) return;
        // The page's drop handler (audio upload) must not see it.
        e.preventDefault();
        e.stopPropagation();
        setDropOver(false);
        onActivate();
        attachFiles(Array.from(e.dataTransfer.files), false);
    };

    /** A waveform pin was clicked: open the section and point at the attachment / mark. */
    const showAttachment = (id: string) => {
        setAttachmentsOpen(true);
        setHighlightedId(id);
        requestAnimationFrame(() =>
            (document.getElementById(`attachment-${id}`) ?? document.getElementById(`mark-${id}`))?.scrollIntoView({ block: "nearest", behavior: "smooth" }));
        setTimeout(() => setHighlightedId((current) => (current === id ? null : current)), 2000);
    };
    const open = (a: AttachmentDto) => openAttachment(recording.id, a, setPreviewing);
    const ratioOf = (seconds: number) => (recording.durationMs > 0 ? seconds * 1000 / recording.durationMs : 0);
    const markers = [
        ...attachments.filter(isDuringCall).map((a) => ({
            id: a.id,
            kind: "attachment" as const,
            ratio: ratioOf(a.offsetSeconds ?? 0),
            label: a.fileName,
            thumbnailUrl: a.kind === "image" ? attachmentUrl(recording.id, a.id) : undefined,
        })),
        ...marks.map((m) => ({
            id: m.id,
            kind: "mark" as const,
            icon: markKindOf(m.kind).icon,
            ratio: ratioOf(m.offsetSeconds),
            label: `${markKindOf(m.kind).label}: ${m.note ?? (m.pending ? "analyzing in Plaud…" : "no note")}`,
        })),
    ];

    const run = async (action: () => Promise<void>, failure: string) => {
        setBusy(true);
        try {
            await action();
            onChanged();
        } catch {
            onError(failure);
        } finally {
            setBusy(false);
        }
    };

    const process = () => run(() => processRecording(recording.id, workflow), "Could not start processing.");
    const retry = () => run(() => retryRecording(recording.id), "Could not retry the recording.");
    const reprocess = () => run(() => reprocessRecording(recording.id, workflow), "Could not reprocess the recording.");
    const uploaded = recording.source === "upload";
    const remove = () => {
        const name = recording.title ?? recording.filename;
        const attached = attachments.length + marks.length > 0 ? " Its attachments and marks are removed too." : "";
        const consequence = (uploaded
            ? "The uploaded audio is removed for good; result files in the output folder are kept."
            : "The recording stays in Plaud (Loopback ignores it from now on) and result files in the output folder are kept.") + attached;
        if (!window.confirm(`Delete "${name}" from Loopback?

${consequence}`)) return;
        run(() => deleteRecording(recording.id), "Could not delete the recording.");
    };

    const workflowSelect = (
        <select
            className="select select-sm join-item w-44"
            value={workflow}
            onChange={(e) => setWorkflow(e.target.value)}
            disabled={workflows.length === 0}
        >
            {workflows.length === 0 && <option value="">No workflows in workflows/</option>}
            {workflows.map((n) => <option key={n.name} value={n.name}>{n.name}</option>)}
        </select>
    );

    const status = recording.status;
    const inProgress = steps.includes(status) && status !== "done";

    const style = statusStyles[status] ?? statusStyles.synced;
    const stepIndex = steps.indexOf(status);

    return (
        <div
            className={`surface relative rounded-box transition hover:border-base-content/20 hover:shadow-xl hover:shadow-primary/5 ${
                dropOver ? "ring-2 ring-accent" : dragging ? "outline-2 outline-dashed outline-offset-2 outline-accent/50" : active ? "ring-1 ring-primary/25" : ""}`}
            data-attach-target
            onPointerDown={onActivate}
            onFocusCapture={onActivate}
            onDragOver={onDragOver}
            onDragLeave={onDragLeave}
            onDrop={onDrop}
        >
            {dropOver && (
                <div className="pointer-events-none absolute inset-0 z-20 flex items-center justify-center rounded-box bg-base-100/85">
                    <p className="flex items-center gap-2 font-medium text-accent">
                        <i className="fa-solid fa-paperclip"></i>Drop to attach to “{recording.title ?? recording.filename}”
                    </p>
                </div>
            )}
            <div className="flex flex-col gap-4 p-4 sm:p-5">
                <div className="flex items-start gap-3">
                    <div className={`flex size-10 shrink-0 items-center justify-center rounded-xl ${style.tile}`}>
                        <i className={`fa-solid ${style.icon}`}></i>
                    </div>
                    <div className="min-w-0 flex-1">
                        <h3 className="truncate font-semibold tracking-tight">{recording.title ?? recording.filename}</h3>
                        <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-base-content/60">
                            <span><i className="fa-regular fa-calendar mr-1.5"></i>{formatDateTime(recording.startTime)}</span>
                            <span><i className="fa-regular fa-clock mr-1.5"></i>{formatDuration(recording.durationMs)}</span>
                            {uploaded && (
                                <span className="rounded-full bg-base-content/8 px-2 py-0.5 font-medium" title="Uploaded audio file, not from Plaud">
                                    <i className="fa-solid fa-upload mr-1.5"></i>Uploaded
                                </span>
                            )}
                            {attachments.length > 0 && (
                                <button
                                    className="rounded-full bg-accent/12 px-2 py-0.5 font-medium text-accent hover:bg-accent/20"
                                    onClick={() => setAttachmentsOpen(true)}
                                    title="Attachments"
                                >
                                    <i className="fa-solid fa-paperclip mr-1.5"></i>{attachments.length}
                                </button>
                            )}
                            {marks.length > 0 && (
                                <button
                                    className="rounded-full bg-warning/15 px-2 py-0.5 font-medium text-warning hover:bg-warning/25"
                                    onClick={() => setAttachmentsOpen(true)}
                                    title="Marks made in Plaud"
                                >
                                    <i className="fa-solid fa-flag mr-1.5"></i>{marks.length}
                                </button>
                            )}
                            {recording.workflow && (
                                <span className="rounded-full bg-base-content/8 px-2 py-0.5 font-medium">
                                    <i className="fa-solid fa-diagram-project mr-1.5"></i>{recording.workflow}
                                </span>
                            )}
                        </div>
                    </div>
                    <span className={`badge badge-soft ${style.badge} gap-1.5 rounded-full`}>
                        {inProgress && <span className="loading loading-spinner loading-xs"></span>}
                        {stepLabels[status] ?? status}
                    </span>
                    <button
                        className="btn btn-ghost btn-sm btn-square rounded-lg text-base-content/50 hover:text-accent"
                        onClick={() => fileInput.current?.click()}
                        aria-label="Attach files"
                        title="Attach files (or drop them on this card, or paste with Ctrl+V)"
                    >
                        <i className="fa-solid fa-paperclip"></i>
                    </button>
                    <input
                        ref={fileInput}
                        type="file"
                        multiple
                        hidden
                        onChange={(e) => {
                            attachFiles(Array.from(e.target.files ?? []), false);
                            e.target.value = "";
                        }}
                    />
                    <button className="btn btn-ghost btn-sm btn-square rounded-lg text-base-content/50 hover:text-error" onClick={remove} disabled={busy} aria-label="Delete" title="Delete from Loopback">
                        <i className="fa-regular fa-trash-can"></i>
                    </button>
                </div>

                {inProgress && (
                    <div className="grid grid-cols-4 gap-2">
                        {steps.map((s, i) => (
                            <div key={s} className="space-y-1.5">
                                <div className={`h-1.5 rounded-full ${i < stepIndex ? "bg-gradient-brand" : i === stepIndex ? "bg-gradient-brand animate-pulse" : "bg-base-content/10"}`}></div>
                                <div className={`text-[11px] ${i <= stepIndex ? "font-medium text-base-content/80" : "text-base-content/40"}`}>{stepLabels[s]}</div>
                            </div>
                        ))}
                    </div>
                )}

                {status === "failed" && (
                    <div role="alert" className="alert alert-error alert-soft text-sm">
                        <i className="fa-solid fa-triangle-exclamation"></i>
                        <span>
                            Failed while {stepLabels[recording.failedStep ?? ""]?.toLowerCase() ?? "processing"}: {recording.error}
                        </span>
                        <button className="btn btn-sm" onClick={retry} disabled={busy}>
                            <i className="fa-solid fa-rotate-right"></i>Retry
                        </button>
                    </div>
                )}

                {/* Playable in every state - the audio is stored locally until the file is deleted in Plaud (uploads: in Loopback). */}
                <div className="rounded-xl bg-base-200/60 px-3 py-2">
                    <AudioPlayer
                        ref={playerRef}
                        recordingId={recording.id}
                        durationMs={recording.durationMs}
                        peaks={recording.peaks}
                        markers={markers}
                        onMarkerClick={showAttachment}
                        onError={onError}
                    />
                </div>

                {(attachments.length > 0 || marks.length > 0 || uploads.length > 0) && (
                    <details
                        className="collapse collapse-arrow rounded-xl border border-base-content/8 bg-base-200/40"
                        open={attachmentsOpen}
                        onToggle={(e) => setAttachmentsOpen(e.currentTarget.open)}
                    >
                        <summary className="collapse-title min-h-0 py-3 text-sm font-semibold">
                            <i className="fa-solid fa-paperclip mr-2 text-accent"></i>{marks.length > 0 ? "Marks & attachments" : "Attachments"}
                            <span className="ml-2 rounded-full bg-base-content/8 px-2 py-0.5 text-xs font-medium">{marks.length + attachments.length}</span>
                        </summary>
                        <div className="collapse-content text-sm">
                            {marks.length > 0 && (
                                <MarksList
                                    marks={marks}
                                    highlightedId={highlightedId}
                                    onSeek={(seconds) => playerRef.current?.seekTo(seconds)}
                                    onChanged={onChanged}
                                    onError={onError}
                                />
                            )}
                            <AttachmentsView
                                recordingId={recording.id}
                                durationMs={recording.durationMs}
                                attachments={attachments}
                                uploads={uploads}
                                highlightedId={highlightedId}
                                onOpen={open}
                                onSeek={(seconds) => playerRef.current?.seekTo(seconds)}
                                playbackSeconds={() => playerRef.current?.positionSeconds() ?? 0}
                                onDismissUpload={dismissUpload}
                                onChanged={onChanged}
                                onError={onError}
                            />
                        </div>
                    </details>
                )}

                {status === "done" && recording.result && (
                    <details className="collapse collapse-arrow rounded-xl border border-base-content/8 bg-base-200/40">
                        <summary className="collapse-title min-h-0 py-3 text-sm font-semibold">
                            <i className="fa-solid fa-file-lines mr-2 text-success"></i>Result
                            {recording.outputFile && <code className="ml-2 rounded bg-base-content/8 px-1.5 py-0.5 font-mono text-xs font-normal text-base-content/60">{recording.outputFile}</code>}
                        </summary>
                        <div className="collapse-content text-sm">
                            <ResultView
                                result={recording.result}
                                onSeek={(seconds) => playerRef.current?.seekTo(seconds)}
                                onOpenAttachment={(fileName) => {
                                    const a = attachments.find((x) => x.fileName === fileName);
                                    if (a) open(a);
                                    else onError(`The attachment "${fileName}" is no longer in Loopback.`);
                                }}
                            />
                        </div>
                    </details>
                )}

                {recording.hasTranscript && (
                    <details
                        className="collapse collapse-arrow rounded-xl border border-base-content/8 bg-base-200/40"
                        onToggle={(e) => { if (e.currentTarget.open) setTranscriptOpened(true); }}
                    >
                        <summary className="collapse-title min-h-0 py-3 text-sm font-semibold">
                            <i className="fa-solid fa-comments mr-2 text-info"></i>Transcript
                        </summary>
                        <div className="collapse-content text-sm">
                            {/* Loaded on first open only - a long recording has hundreds of segments. */}
                            {transcriptOpened && <TranscriptView recordingId={recording.id} attachments={attachments} marks={marks} onOpenAttachment={open} onError={onError} />}
                        </div>
                    </details>
                )}

                {status === "done" && recording.attachmentsOutdated && (
                    <div role="status" className="alert alert-warning alert-soft text-sm">
                        <i className="fa-solid fa-paperclip"></i>
                        <span>Marks or attachments changed since processing - Reprocess to include them.</span>
                    </div>
                )}

                {status === "done" && recording.canReprocess && (
                    <div className="flex justify-end">
                        <div className="join">
                            {workflowSelect}
                            <button className="btn btn-sm join-item" onClick={reprocess} disabled={busy || !workflow} title="Run the workflow again on the stored transcript (no new transcription)">
                                {busy ? <span className="loading loading-spinner loading-xs"></span> : <i className="fa-solid fa-rotate"></i>}
                                Reprocess
                            </button>
                        </div>
                    </div>
                )}

                {status === "synced" && (
                    <div className="flex justify-end">
                        <div className="join">
                            {workflowSelect}
                            <button className="btn btn-sm btn-primary join-item" onClick={process} disabled={busy || !workflow}>
                                {busy ? <span className="loading loading-spinner loading-xs"></span> : <i className="fa-solid fa-wand-magic-sparkles"></i>}
                                Process
                            </button>
                        </div>
                    </div>
                )}
            </div>

            {previewing && <Lightbox recordingId={recording.id} attachment={previewing} onClose={() => setPreviewing(null)} />}
        </div>
    );
}
