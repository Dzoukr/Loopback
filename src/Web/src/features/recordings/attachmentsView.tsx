"use client";

import { useEffect, useState } from "react";
import { createPortal } from "react-dom";
import type { AttachmentDto, MarkDto } from "@/lib/generated/api-client";
import { addMark, deleteAttachment, deleteMark, setAttachmentNote, setAttachmentTime, setMarkNote } from "./actions";
import { attachmentUrl, formatOffset, formatSize, isDuringCall, kindIcons, markKindOf, parseOffset, placementLabel } from "./attachments";
import { formatClock } from "./utils";

/** An attachment being uploaded (shown as a tile until the list refresh brings the real one). */
export type UploadTile = {
    key: string;
    name: string;
    progress: number;
    state: "uploading" | "error";
    error?: string;
};

const timeSourceLabels: Record<string, string> = {
    plaud: "reported by Plaud",
    filename: "from the file name",
    exif: "from the photo's EXIF data",
    modified: "from the file's modification time",
    manual: "set manually",
    none: "unknown",
};

/** Opens the attachment the way its kind allows: images in the lightbox, PDF / text in a new tab, anything else downloads. */
export function openAttachment(recordingId: string, a: AttachmentDto, showImage: (a: AttachmentDto) => void) {
    if (a.kind === "image") showImage(a);
    else if (a.kind === "pdf" || a.kind === "text") window.open(attachmentUrl(recordingId, a.id), "_blank", "noopener");
    else window.location.href = attachmentUrl(recordingId, a.id, true);
}

/** Audio (e.g. a Plaud voice memo) plays right in the card. */
export function isAudio(a: AttachmentDto): boolean {
    return a.contentType.startsWith("audio/");
}

/** Small preview: the image itself or the kind's icon. */
export function AttachmentThumb({ recordingId, attachment, className }: { recordingId: string; attachment: AttachmentDto; className: string }) {
    return attachment.kind === "image"
        // eslint-disable-next-line @next/next/no-img-element -- served by the BFF route, no Next image optimization
        ? <img src={attachmentUrl(recordingId, attachment.id)} alt={attachment.fileName} loading="lazy" className={`object-cover ${className}`} />
        : (
            <span className={`flex items-center justify-center bg-base-content/5 text-base-content/40 ${className}`}>
                <i className={`fa-regular ${isAudio(attachment) ? "fa-file-audio" : kindIcons[attachment.kind] ?? kindIcons.other}`}></i>
            </span>
        );
}

/** Marked in Plaud, imported during sync. */
export function PlaudBadge() {
    return <span className="shrink-0 rounded bg-secondary/15 px-1 text-[10px] font-semibold text-secondary" title="Imported from a mark made in Plaud">Plaud</span>;
}

type MarksProps = {
    marks: MarkDto[];
    highlightedId: string | null;
    onSeek: (seconds: number) => void;
    onChanged: () => void;
    onError: (message: string) => void;
};

/** Moments marked in Plaud during the call (device highlights, text notes, voice memos) and notes added in Loopback. */
export function MarksList({ marks, highlightedId, onSeek, onChanged, onError }: MarksProps) {
    const [editing, setEditing] = useState<{ id: string; note: string } | null>(null);
    const [saving, setSaving] = useState(false);

    const save = async () => {
        if (!editing) return;
        setSaving(true);
        try {
            await setMarkNote(editing.id, editing.note);
            setEditing(null);
            onChanged();
        } catch {
            onError("Could not save the mark.");
        } finally {
            setSaving(false);
        }
    };

    const remove = async (m: MarkDto) => {
        if (!window.confirm(m.source === "plaud" ? "Delete this mark? It will not be imported from Plaud again." : "Delete this note?")) return;
        try {
            await deleteMark(m.id);
            onChanged();
        } catch {
            onError("Could not delete the mark.");
        }
    };

    return (
        <ul className="mb-3 space-y-1.5">
            {marks.map((m) => (
                <li
                    key={m.id}
                    id={`mark-${m.id}`}
                    className={`group flex items-start gap-2 rounded-lg border px-2 py-1.5 transition ${
                        highlightedId === m.id ? "border-warning ring-2 ring-warning/40" : "border-base-content/8 bg-base-100"}`}
                >
                    {m.offsetSeconds !== null ? (
                        <button
                            className="shrink-0 rounded-full bg-warning/15 px-1.5 py-0.5 font-mono text-[11px] font-medium text-warning hover:bg-warning/25"
                            onClick={() => onSeek(m.offsetSeconds ?? 0)}
                            title={`${markKindOf(m.kind).label} - jump to this moment`}
                        >
                            <i className={`fa-solid ${markKindOf(m.kind).icon} mr-1`}></i>{formatClock(m.offsetSeconds)}
                        </button>
                    ) : (
                        <span className="shrink-0 rounded-full bg-base-content/8 px-1.5 py-0.5 text-[11px] font-medium text-base-content/70" title={`${markKindOf(m.kind).label} about the call as a whole`}>
                            <i className={`fa-solid ${markKindOf(m.kind).icon} mr-1`}></i>No time
                        </span>
                    )}
                    {editing?.id === m.id ? (
                        <div className="flex flex-1 flex-col gap-1.5">
                            <textarea
                                className="textarea textarea-sm w-full"
                                rows={2}
                                maxLength={1000}
                                value={editing.note}
                                onChange={(e) => setEditing({ id: m.id, note: e.target.value })}
                                autoFocus
                                aria-label="Mark note"
                            />
                            <div className="flex justify-end gap-1">
                                <button className="btn btn-ghost btn-xs" onClick={() => setEditing(null)} disabled={saving}>Cancel</button>
                                <button className="btn btn-primary btn-xs" onClick={save} disabled={saving}>
                                    {saving && <span className="loading loading-spinner loading-xs"></span>}Save
                                </button>
                            </div>
                        </div>
                    ) : (
                        <>
                            <p className="min-w-0 flex-1 text-xs">
                                {m.note ?? <span className="italic text-base-content/50">{m.pending ? "Analyzing in Plaud…" : "No note"}</span>}
                            </p>
                            {m.source === "plaud" && <PlaudBadge />}
                            <div className="flex shrink-0 gap-0.5 opacity-0 transition group-focus-within:opacity-100 group-hover:opacity-100">
                                <button className="btn btn-ghost btn-xs btn-square" onClick={() => setEditing({ id: m.id, note: m.note ?? "" })} aria-label="Edit note" title="Edit note">
                                    <i className="fa-solid fa-pen"></i>
                                </button>
                                <button className="btn btn-ghost btn-xs btn-square hover:text-error" onClick={() => remove(m)} aria-label="Delete mark" title="Delete">
                                    <i className="fa-regular fa-trash-can"></i>
                                </button>
                            </div>
                        </>
                    )}
                </li>
            ))}
        </ul>
    );
}

type Props = {
    recordingId: string;
    durationMs: number;
    attachments: AttachmentDto[];
    uploads: UploadTile[];
    /** Briefly highlighted tile (a waveform pin was clicked). */
    highlightedId: string | null;
    onOpen: (a: AttachmentDto) => void;
    onSeek: (seconds: number) => void;
    /** Current player position in seconds (for "Use playback position"). */
    playbackSeconds: () => number;
    onDismissUpload: (key: string) => void;
    onChanged: () => void;
    onError: (message: string) => void;
};

/** Grid of the recording's attachments: during the call by time, then references. */
export function AttachmentsView({ recordingId, durationMs, attachments, uploads, highlightedId, onOpen, onSeek, playbackSeconds, onDismissUpload, onChanged, onError }: Props) {
    const [editing, setEditing] = useState<AttachmentDto | null>(null);

    const remove = async (a: AttachmentDto) => {
        if (!window.confirm(`Delete the attachment "${a.fileName}"?`)) return;
        try {
            await deleteAttachment(a.id);
            onChanged();
        } catch {
            onError("Could not delete the attachment.");
        }
    };

    return (
        <>
            <ul className="grid grid-cols-2 gap-2 sm:grid-cols-3 md:grid-cols-4">
                {attachments.map((a) => (
                    <li
                        key={a.id}
                        id={`attachment-${a.id}`}
                        className={`group relative overflow-hidden rounded-xl border bg-base-100 transition ${
                            highlightedId === a.id ? "border-primary ring-2 ring-primary/50" : "border-base-content/8 hover:border-base-content/20"}`}
                    >
                        {isAudio(a) ? (
                            <div className="flex aspect-video w-full flex-col items-center justify-center gap-2 bg-base-content/5 px-2 text-2xl text-base-content/40">
                                <i className="fa-solid fa-microphone-lines"></i>
                                <audio controls preload="none" src={attachmentUrl(recordingId, a.id)} className="h-8 w-full" aria-label={`Play ${a.fileName}`} />
                            </div>
                        ) : (
                            <button className="block w-full cursor-zoom-in" onClick={() => onOpen(a)} title={`Open ${a.fileName}`}>
                                <AttachmentThumb recordingId={recordingId} attachment={a} className="aspect-video w-full text-3xl" />
                            </button>
                        )}
                        <div className="space-y-1 p-2">
                            <div className="flex items-center gap-1">
                                <span className="truncate text-xs font-medium" title={a.fileName}>{a.fileName}</span>
                                {a.source === "plaud" && <PlaudBadge />}
                            </div>
                            <div className="flex items-center gap-1.5 text-[11px] text-base-content/60">
                                {isDuringCall(a) ? (
                                    <button
                                        className="rounded-full bg-primary/12 px-1.5 py-0.5 font-mono font-medium text-primary hover:bg-primary/20"
                                        onClick={() => onSeek(a.offsetSeconds ?? 0)}
                                        title="Jump to this moment"
                                    >
                                        <i className="fa-solid fa-location-dot mr-1"></i>{placementLabel(a, formatClock)}
                                    </button>
                                ) : (
                                    <span className="rounded-full bg-base-content/8 px-1.5 py-0.5 font-medium" title="Belongs to the call as a whole">{placementLabel(a, formatClock)}</span>
                                )}
                                <span className="ml-auto shrink-0">{formatSize(a.sizeBytes)}</span>
                            </div>
                            {a.note && <p className="line-clamp-2 text-[11px] text-base-content/70" title={a.note}>{a.note}</p>}
                            {a.description && (
                                <p className="line-clamp-3 text-[11px] italic text-base-content/55" title={a.description}>
                                    <i className={`fa-solid ${isAudio(a) ? "fa-quote-left" : "fa-wand-magic-sparkles"} mr-1`}></i>{a.description}
                                </p>
                            )}
                        </div>
                        <div className="absolute right-1 top-1 flex gap-0.5 rounded-lg bg-base-100/90 p-0.5 opacity-0 shadow transition group-focus-within:opacity-100 group-hover:opacity-100">
                            <button className="btn btn-ghost btn-xs btn-square" onClick={() => setEditing(a)} aria-label={`Edit ${a.fileName}`} title="Edit time and note">
                                <i className="fa-solid fa-pen"></i>
                            </button>
                            <a className="btn btn-ghost btn-xs btn-square" href={attachmentUrl(recordingId, a.id, true)} aria-label={`Download ${a.fileName}`} title="Download">
                                <i className="fa-solid fa-download"></i>
                            </a>
                            <button className="btn btn-ghost btn-xs btn-square hover:text-error" onClick={() => remove(a)} aria-label={`Delete ${a.fileName}`} title="Delete">
                                <i className="fa-regular fa-trash-can"></i>
                            </button>
                        </div>
                    </li>
                ))}
                {uploads.map((u) => (
                    <li key={u.key} className="flex flex-col justify-between gap-2 rounded-xl border border-dashed border-base-content/15 p-2">
                        <div className="truncate text-xs font-medium" title={u.name}>{u.name}</div>
                        {u.state === "uploading" ? (
                            <progress className="progress progress-primary" value={u.progress < 1 ? u.progress * 100 : undefined} max={100}></progress>
                        ) : (
                            <div className="flex items-start gap-1 text-[11px] text-error">
                                <span className="flex-1">{u.error}</span>
                                <button className="btn btn-ghost btn-xs btn-square" onClick={() => onDismissUpload(u.key)} aria-label="Dismiss">
                                    <i className="fa-solid fa-xmark"></i>
                                </button>
                            </div>
                        )}
                    </li>
                ))}
            </ul>

            {editing && (
                <EditDialog
                    attachment={editing}
                    durationSeconds={durationMs / 1000}
                    playbackSeconds={playbackSeconds}
                    onClose={() => setEditing(null)}
                    onSaved={() => { setEditing(null); onChanged(); }}
                    onError={onError}
                />
            )}
        </>
    );
}

type Mode = "offset" | "reference" | "detected";

function EditDialog({ attachment, durationSeconds, playbackSeconds, onClose, onSaved, onError }: {
    attachment: AttachmentDto;
    durationSeconds: number;
    playbackSeconds: () => number;
    onClose: () => void;
    onSaved: () => void;
    onError: (message: string) => void;
}) {
    const initialMode: Mode = isDuringCall(attachment) ? "offset" : "reference";
    const [mode, setMode] = useState<Mode>(initialMode);
    const [time, setTime] = useState(formatOffset(attachment.offsetSeconds ?? playbackSeconds()));
    const [note, setNote] = useState(attachment.note ?? "");
    const [saving, setSaving] = useState(false);

    const offset = parseOffset(time);
    const offsetValid = offset !== null && offset <= Math.ceil(durationSeconds);
    const timeChanged = mode === "detected" || mode !== initialMode || (mode === "offset" && offset !== Math.round(attachment.offsetSeconds ?? -1));
    const noteChanged = note.trim() !== (attachment.note ?? "");

    useEffect(() => {
        const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !saving) onClose(); };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [onClose, saving]);

    const save = async () => {
        setSaving(true);
        try {
            if (timeChanged) await setAttachmentTime(attachment.id, mode, mode === "offset" ? Math.min(offset ?? 0, durationSeconds) : 0);
            if (noteChanged) await setAttachmentNote(attachment.id, note);
            onSaved();
        } catch {
            onError("Could not save the attachment.");
            setSaving(false);
        }
    };

    // Portal: the card's backdrop-filter would confine the fixed modal to the card.
    return createPortal(
        <dialog className="modal modal-open" aria-labelledby="attachment-edit-title">
            <div className="modal-box max-w-md">
                <h3 id="attachment-edit-title" className="flex items-center gap-2 text-lg font-semibold">
                    <i className="fa-solid fa-paperclip text-primary"></i>
                    <span className="truncate">{attachment.fileName}</span>
                </h3>
                <p className="mt-1 text-sm text-base-content/60">
                    Captured {attachment.capturedAt ? new Date(attachment.capturedAt).toLocaleString() : "at an unknown time"} - {timeSourceLabels[attachment.timeSource] ?? attachment.timeSource}.
                </p>

                <fieldset className="mt-4 space-y-2">
                    <legend className="mb-1 text-sm font-semibold">Placement</legend>
                    <label className="flex flex-wrap items-center gap-2 text-sm">
                        <input type="radio" className="radio radio-sm radio-primary" checked={mode === "offset"} onChange={() => setMode("offset")} />
                        At
                        <input
                            className={`input input-sm w-28 font-mono ${mode === "offset" && !offsetValid ? "input-error" : ""}`}
                            value={time}
                            onChange={(e) => { setTime(e.target.value); setMode("offset"); }}
                            placeholder="hh:mm:ss"
                            aria-label="Time in the recording"
                        />
                        <button className="btn btn-ghost btn-xs" onClick={() => { setTime(formatOffset(playbackSeconds())); setMode("offset"); }} title="Set to where the player is now">
                            <i className="fa-solid fa-crosshairs"></i>Use playback position
                        </button>
                    </label>
                    {mode === "offset" && !offsetValid && (
                        <p className="pl-7 text-xs text-error">Enter a time within the recording (up to {formatOffset(durationSeconds)}).</p>
                    )}
                    <label className="flex items-center gap-2 text-sm">
                        <input type="radio" className="radio radio-sm radio-primary" checked={mode === "reference"} onChange={() => setMode("reference")} />
                        Reference - the call as a whole
                    </label>
                    <label className="flex items-center gap-2 text-sm">
                        <input type="radio" className="radio radio-sm radio-primary" checked={mode === "detected"} onChange={() => setMode("detected")} />
                        {attachment.source === "plaud" ? "Back to the time reported by Plaud" : "Detect the time again from the file"}
                    </label>
                </fieldset>

                <label className="mt-4 block text-sm font-semibold" htmlFor="attachment-note">Note</label>
                <textarea
                    id="attachment-note"
                    className="textarea mt-1 w-full"
                    rows={3}
                    maxLength={1000}
                    value={note}
                    onChange={(e) => setNote(e.target.value)}
                    placeholder="What it shows, why it matters…"
                />

                <div className="modal-action">
                    <button className="btn btn-sm btn-ghost" onClick={onClose} disabled={saving}>Cancel</button>
                    <button
                        className="btn btn-sm btn-primary"
                        onClick={save}
                        disabled={saving || (!timeChanged && !noteChanged) || (mode === "offset" && !offsetValid)}
                    >
                        {saving && <span className="loading loading-spinner loading-xs"></span>}Save
                    </button>
                </div>
            </div>
            <div className="modal-backdrop bg-base-300/60" onClick={() => { if (!saving) onClose(); }}></div>
        </dialog>,
        document.body,
    );
}

/**
 * Adds a note at `initialSeconds` (where the user put the player), or without a time when the player
 * was not positioned (null) - both can be changed in the dialog.
 */
export function AddNoteDialog({ recordingId, durationSeconds, initialSeconds, playbackSeconds, onClose, onSaved, onError }: {
    recordingId: string;
    durationSeconds: number;
    initialSeconds: number | null;
    playbackSeconds: () => number;
    onClose: () => void;
    onSaved: () => void;
    onError: (message: string) => void;
}) {
    const [atTime, setAtTime] = useState(initialSeconds !== null);
    const [time, setTime] = useState(formatOffset(initialSeconds ?? playbackSeconds()));
    const [note, setNote] = useState("");
    const [saving, setSaving] = useState(false);

    const offset = parseOffset(time);
    const offsetValid = offset !== null && offset <= Math.ceil(durationSeconds);
    const canSave = !saving && note.trim() !== "" && (!atTime || offsetValid);

    useEffect(() => {
        const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !saving) onClose(); };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [onClose, saving]);

    const save = async () => {
        if (!canSave) return;
        setSaving(true);
        try {
            await addMark(recordingId, atTime ? Math.min(offset ?? 0, durationSeconds) : null, note);
            onSaved();
        } catch {
            onError("Could not add the note.");
            setSaving(false);
        }
    };

    // Portal: the card's backdrop-filter would confine the fixed modal to the card.
    return createPortal(
        <dialog className="modal modal-open" aria-labelledby="note-add-title">
            <div className="modal-box max-w-md">
                <h3 id="note-add-title" className="flex items-center gap-2 text-lg font-semibold">
                    <i className={`fa-solid ${markKindOf("note").icon} text-warning`}></i>Add note
                </h3>

                <textarea
                    className="textarea mt-4 w-full"
                    rows={3}
                    maxLength={1000}
                    value={note}
                    onChange={(e) => setNote(e.target.value)}
                    onKeyDown={(e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) save(); }}
                    placeholder="What matters here…"
                    aria-label="Note"
                    autoFocus
                />

                <fieldset className="mt-3 space-y-2">
                    <legend className="mb-1 text-sm font-semibold">Time</legend>
                    <label className="flex flex-wrap items-center gap-2 text-sm">
                        <input type="radio" className="radio radio-sm radio-primary" checked={atTime} onChange={() => setAtTime(true)} />
                        At
                        <input
                            className={`input input-sm w-28 font-mono ${atTime && !offsetValid ? "input-error" : ""}`}
                            value={time}
                            onChange={(e) => { setTime(e.target.value); setAtTime(true); }}
                            placeholder="hh:mm:ss"
                            aria-label="Time in the recording"
                        />
                        <button className="btn btn-ghost btn-xs" onClick={() => { setTime(formatOffset(playbackSeconds())); setAtTime(true); }} title="Set to where the player is now">
                            <i className="fa-solid fa-crosshairs"></i>Use playback position
                        </button>
                    </label>
                    {atTime && !offsetValid && (
                        <p className="pl-7 text-xs text-error">Enter a time within the recording (up to {formatOffset(durationSeconds)}).</p>
                    )}
                    <label className="flex items-center gap-2 text-sm">
                        <input type="radio" className="radio radio-sm radio-primary" checked={!atTime} onChange={() => setAtTime(false)} />
                        No time - the call as a whole
                    </label>
                </fieldset>

                <div className="modal-action">
                    <button className="btn btn-sm btn-ghost" onClick={onClose} disabled={saving}>Cancel</button>
                    <button className="btn btn-sm btn-primary" onClick={save} disabled={!canSave}>
                        {saving && <span className="loading loading-spinner loading-xs"></span>}Add
                    </button>
                </div>
            </div>
            <div className="modal-backdrop bg-base-300/60" onClick={() => { if (!saving) onClose(); }}></div>
        </dialog>,
        document.body,
    );
}

/** Full-size image preview. */
export function Lightbox({ recordingId, attachment, onClose }: { recordingId: string; attachment: AttachmentDto; onClose: () => void }) {
    useEffect(() => {
        const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [onClose]);

    // Portal: the card's backdrop-filter would confine the fixed modal to the card.
    return createPortal(
        <dialog className="modal modal-open" aria-label={attachment.fileName}>
            <div className="modal-box flex max-h-[95vh] w-auto max-w-[95vw] flex-col gap-3 p-3">
                {/* eslint-disable-next-line @next/next/no-img-element -- served by the BFF route */}
                <img src={attachmentUrl(recordingId, attachment.id)} alt={attachment.fileName} className="max-h-[80vh] min-h-0 rounded-lg object-contain" />
                <div className="flex items-center gap-3 text-sm">
                    <div className="min-w-0 flex-1">
                        <div className="truncate font-medium">{attachment.fileName}</div>
                        <div className="text-xs text-base-content/60">
                            {placementLabel(attachment, formatClock)}{attachment.note ? ` - ${attachment.note}` : ""}
                        </div>
                        {attachment.description && <div className="mt-1 max-w-2xl text-xs italic text-base-content/60">{attachment.description}</div>}
                    </div>
                    <a className="btn btn-ghost btn-sm" href={attachmentUrl(recordingId, attachment.id, true)}>
                        <i className="fa-solid fa-download"></i>Download
                    </a>
                    <button className="btn btn-sm" onClick={onClose}>Close</button>
                </div>
            </div>
            <div className="modal-backdrop bg-base-300/80" onClick={onClose}></div>
        </dialog>,
        document.body,
    );
}
