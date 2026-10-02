"use client";

import type { SyncStatusDto } from "@/lib/generated/api-client";
import { formatDateTime } from "./utils";

type ControlsProps = {
    status: SyncStatusDto | null;
    syncing: boolean;
    onSyncNow: () => void;
};

/** Connection pill + last sync + "Sync now" (lives in the top bar). */
export function SyncControls({ status, syncing, onSyncNow }: ControlsProps) {
    const connected = status?.connected ?? false;

    return (
        <div className="flex items-center gap-2 sm:gap-3">
            <a
                href="https://web.plaud.ai/"
                target="_blank"
                rel="noopener noreferrer"
                className="hidden items-center gap-2 rounded-full border border-base-content/10 bg-base-100/60 px-3 py-1.5 text-xs transition-colors hover:border-base-content/25 hover:bg-base-100 sm:flex"
                title={`${status?.account ? `Plaud account: ${status.account}\n` : ""}Last sync: ${formatDateTime(status?.lastSyncAt)}\nOpen web.plaud.ai`}
            >
                <span className="relative flex size-2">
                    {connected && <span className="absolute inline-flex size-full animate-ping rounded-full bg-success opacity-60"></span>}
                    <span className={`relative inline-flex size-2 rounded-full ${connected ? "bg-success" : "bg-error"}`}></span>
                </span>
                <span className="font-medium">{connected ? "Plaud connected" : "Plaud offline"}</span>
                <span className="text-base-content/50">· {formatDateTime(status?.lastSyncAt)}</span>
                <i className="fa-solid fa-arrow-up-right-from-square text-[0.65rem] text-base-content/40"></i>
            </a>
            <button className="btn btn-sm btn-primary rounded-full" onClick={onSyncNow} disabled={syncing}>
                {syncing
                    ? <span className="loading loading-spinner loading-xs"></span>
                    : <i className="fa-solid fa-arrows-rotate"></i>}
                Sync now
            </button>
        </div>
    );
}

/** Sync error (the Plaud session renews itself, so only failures need attention). */
export function SyncAlerts({ status }: { status: SyncStatusDto | null }) {
    if (!status?.lastError) return null;

    return (
        <div role="alert" className="alert alert-error alert-soft">
            <i className="fa-solid fa-circle-exclamation"></i>
            <span>{status.lastError}</span>
        </div>
    );
}
