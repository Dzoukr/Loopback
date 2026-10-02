"use client";

import { useState } from "react";
import type { WorkflowDto, RecordingDto } from "@/lib/generated/api-client";
import { deleteRecording, processRecording, reprocessRecording, retryRecording } from "./actions";
import { AudioPlayer } from "./audioPlayer";
import { ResultView } from "./resultView";
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
    onChanged: () => void;
    onError: (message: string) => void;
};

export function RecordingCard({ recording, workflows, onChanged, onError }: Props) {
    const [chosenWorkflow, setWorkflow] = useState<string | null>(recording.workflow ?? null);
    // Workflows load after the first render, so fall back to the first one until the user picks.
    const workflow = chosenWorkflow ?? workflows[0]?.name ?? "";
    const [busy, setBusy] = useState(false);

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
    const remove = () => {
        const name = recording.title ?? recording.filename;
        if (!window.confirm(`Delete "${name}" from Loopback?

The recording stays in Plaud (Loopback ignores it from now on) and result files in the output folder are kept.`)) return;
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
        <div className="surface rounded-box transition hover:border-base-content/20 hover:shadow-xl hover:shadow-primary/5">
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

                {/* Playable in every state - the audio is stored locally until the file is deleted in Plaud. */}
                <div className="rounded-xl bg-base-200/60 px-3 py-2">
                    <AudioPlayer recordingId={recording.id} durationMs={recording.durationMs} peaks={recording.peaks} onError={onError} />
                </div>

                {status === "done" && recording.result && (
                    <details className="collapse collapse-arrow rounded-xl border border-base-content/8 bg-base-200/40">
                        <summary className="collapse-title min-h-0 py-3 text-sm font-semibold">
                            <i className="fa-solid fa-file-lines mr-2 text-success"></i>Result
                            {recording.outputFile && <code className="ml-2 rounded bg-base-content/8 px-1.5 py-0.5 font-mono text-xs font-normal text-base-content/60">{recording.outputFile}</code>}
                        </summary>
                        <div className="collapse-content text-sm">
                            <ResultView result={recording.result} />
                        </div>
                    </details>
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
        </div>
    );
}
