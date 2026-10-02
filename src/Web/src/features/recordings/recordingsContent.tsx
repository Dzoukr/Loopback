"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { WorkflowDto, RecordingDto, SyncStatusDto } from "@/lib/generated/api-client";
import { getWorkflows, getRecordings, getSyncStatus, syncNow } from "./actions";
import { RecordingCard } from "./recordingCard";
import { SyncAlerts, SyncControls } from "./syncStatusBar";
import { groupOf, type RecordingGroup } from "./utils";

// The list refreshes itself; processing runs in the backend's background jobs.
const refreshMs = 5000;

const sections: { group: RecordingGroup; title: string; icon: string; tone: string }[] = [
    { group: "inProgress", title: "In progress", icon: "fa-gears", tone: "text-info bg-info/12" },
    { group: "failed", title: "Failed", icon: "fa-triangle-exclamation", tone: "text-error bg-error/12" },
    { group: "new", title: "New", icon: "fa-microphone-lines", tone: "text-primary bg-primary/12" },
    { group: "processed", title: "Processed", icon: "fa-circle-check", tone: "text-success bg-success/12" },
];

export function RecordingsContent() {
    const [recordings, setRecordings] = useState<RecordingDto[] | null>(null);
    const [workflows, setWorkflows] = useState<WorkflowDto[]>([]);
    const [syncStatus, setSyncStatus] = useState<SyncStatusDto | null>(null);
    const [syncing, setSyncing] = useState(false);
    const [error, setError] = useState<string | null>(null);
    // null = show every group; clicking a tile shows only that one, clicking it again resets.
    const [filter, setFilter] = useState<RecordingGroup | null>(null);

    const workflowsLoaded = useRef(false);

    const refresh = useCallback(async () => {
        try {
            const [r, s] = await Promise.all([getRecordings(), getSyncStatus()]);
            setRecordings(r);
            setSyncStatus(s);
            setError(null);
            // Workflows load once (a page reload picks up changes), but only after the server
            // answers - it may start after the page (dotnet watch).
            if (!workflowsLoaded.current) {
                setWorkflows(await getWorkflows());
                workflowsLoaded.current = true;
            }
        } catch {
            setError("The Loopback server is not reachable.");
        }
    }, []);

    useEffect(() => {
        refresh();
        const timer = setInterval(refresh, refreshMs);
        return () => clearInterval(timer);
    }, [refresh]);

    const handleSyncNow = async () => {
        setSyncing(true);
        try {
            await syncNow();
            // The sync runs in the background; give it a moment before refreshing.
            setTimeout(() => { refresh().finally(() => setSyncing(false)); }, 3000);
        } catch {
            setError("Could not start the sync.");
            setSyncing(false);
        }
    };

    const count = (group: RecordingGroup) => recordings?.filter((r) => groupOf(r.status) === group).length ?? 0;

    return (
        <>
            <header className="sticky top-0 z-30 border-b border-base-content/8 bg-base-200/70 backdrop-blur-xl">
                <div className="mx-auto flex max-w-5xl items-center gap-3 px-4 py-3 lg:px-8">
                    <div className="flex size-9 items-center justify-center rounded-xl bg-gradient-brand text-primary-content shadow-lg shadow-primary/25">
                        <i className="fa-solid fa-repeat"></i>
                    </div>
                    <div className="leading-tight">
                        <div className="text-lg font-semibold tracking-tight">Loopback</div>
                        <div className="hidden text-xs text-base-content/55 sm:block">Plaud → Speechmatics → Claude → JSON</div>
                    </div>
                    <div className="ml-auto">
                        <SyncControls status={syncStatus} syncing={syncing} onSyncNow={handleSyncNow} />
                    </div>
                </div>
            </header>

            <main className="mx-auto max-w-5xl space-y-8 px-4 py-8 lg:px-8">
                <div>
                    <h1 className="text-3xl font-semibold tracking-tight sm:text-4xl">
                        Your <span className="text-gradient">recordings</span>
                    </h1>
                    <p className="mt-1 text-base-content/60">Synced from Plaud, transcribed, and summarized by your workflows.</p>
                </div>

                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                    {sections.map(({ group, title, icon, tone }) => (
                        <button
                            key={group}
                            onClick={() => setFilter(filter === group ? null : group)}
                            aria-pressed={filter === group}
                            className={`surface group rounded-box p-4 text-left transition hover:-translate-y-0.5 hover:border-base-content/20 ${
                                filter === group ? "ring-2 ring-primary/60" : filter ? "opacity-50 hover:opacity-100" : ""}`}
                        >
                            <div className="flex items-center justify-between">
                                <span className={`flex size-8 items-center justify-center rounded-lg text-sm ${tone}`}>
                                    <i className={`fa-solid ${icon}`}></i>
                                </span>
                                <i className={`fa-solid text-xs transition ${filter === group ? "fa-xmark text-primary" : "fa-filter text-base-content/30 group-hover:text-base-content/60"}`}></i>
                            </div>
                            <div className="mt-3 text-2xl font-semibold tabular-nums">{recordings ? count(group) : "–"}</div>
                            <div className="text-xs text-base-content/60">{title}</div>
                        </button>
                    ))}
                </div>

                <SyncAlerts status={syncStatus} />

                {error && (
                    <div role="alert" className="alert alert-error">
                        <i className="fa-solid fa-circle-exclamation"></i>
                        <span>{error}</span>
                        <button className="btn btn-sm btn-ghost" onClick={() => setError(null)}>Dismiss</button>
                    </div>
                )}

                {recordings === null && (
                    <div className="space-y-3">
                        {[0, 1, 2].map((i) => <div key={i} className="skeleton h-32 w-full rounded-box"></div>)}
                    </div>
                )}

                {recordings?.length === 0 && (
                    <div className="surface rounded-box flex flex-col items-center gap-3 px-6 py-14 text-center">
                        <div className="flex size-14 items-center justify-center rounded-2xl bg-primary/12 text-2xl text-primary">
                            <i className="fa-solid fa-inbox"></i>
                        </div>
                        <p className="font-medium">No recordings yet</p>
                        <p className="text-sm text-base-content/60">New Plaud recordings appear here after the next sync.</p>
                    </div>
                )}

                {recordings && filter && count(filter) === 0 && (
                    <div className="surface rounded-box px-6 py-10 text-center text-sm text-base-content/60">
                        Nothing here.{" "}
                        <button className="link link-primary" onClick={() => setFilter(null)}>Show all recordings</button>
                    </div>
                )}

                {recordings && sections.filter((x) => !filter || x.group === filter).map(({ group, title, icon, tone }) => {
                    const items = recordings.filter((r) => groupOf(r.status) === group);
                    if (items.length === 0) return null;
                    return (
                        <section key={group} className="space-y-3">
                            <h2 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wider text-base-content/60">
                                <span className={`flex size-6 items-center justify-center rounded-md text-xs ${tone}`}>
                                    <i className={`fa-solid ${icon}`}></i>
                                </span>
                                {title}
                                <span className="rounded-full bg-base-content/8 px-2 py-0.5 text-xs font-medium normal-case tracking-normal">{items.length}</span>
                            </h2>
                            {items.map((r) => (
                                <RecordingCard key={r.id} recording={r} workflows={workflows} onChanged={refresh} onError={setError} />
                            ))}
                        </section>
                    );
                })}
            </main>
        </>
    );
}
