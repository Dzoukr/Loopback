"use client";

import { useEffect, useRef, useState } from "react";
import type { AttachmentDto, MarkDto, TranscriptSegmentDto } from "@/lib/generated/api-client";
import { getTranscript } from "./actions";
import { isDuringCall, markKindOf } from "./attachments";
import { AttachmentThumb } from "./attachmentsView";
import { formatClock } from "./utils";

// Per-speaker colors in order of first appearance (full class names, so Tailwind keeps them).
const speakerColors = [
    { label: "text-primary", bar: "border-primary" },
    { label: "text-secondary", bar: "border-secondary" },
    { label: "text-accent", bar: "border-accent" },
    { label: "text-info", bar: "border-info" },
    { label: "text-success", bar: "border-success" },
    { label: "text-warning", bar: "border-warning" },
];

/** Speechmatics labels speakers S1, S2, ... and unknown ones UU. */
function speakerName(speaker: string): string {
    if (speaker === "UU") return "Unknown";
    const match = speaker.match(/^S(\d+)$/);
    return match ? `Speaker ${match[1]}` : speaker;
}

type Props = {
    recordingId: string;
    /** Attachments captured during the call appear between the turns, at their time. */
    attachments: AttachmentDto[];
    /** Marks appear between the turns too. */
    marks: MarkDto[];
    onOpenAttachment: (a: AttachmentDto) => void;
    onError: (message: string) => void;
};

type Placed = { type: "attachment"; item: AttachmentDto; offset: number } | { type: "mark"; item: MarkDto; offset: number };

/** Index of the segment each during-call attachment / mark follows (the last one starting at or before it; -1 = before the first). */
function placeBySegment(segments: TranscriptSegmentDto[], attachments: AttachmentDto[], marks: MarkDto[]): Map<number, Placed[]> {
    const items: Placed[] = [
        ...attachments.filter(isDuringCall).map((a) => ({ type: "attachment" as const, item: a, offset: a.offsetSeconds ?? 0 })),
        ...marks.map((m) => ({ type: "mark" as const, item: m, offset: m.offsetSeconds })),
    ].sort((a, b) => a.offset - b.offset);
    const result = new Map<number, Placed[]>();
    for (const p of items) {
        let index = -1;
        while (index + 1 < segments.length && segments[index + 1].start <= p.offset) index++;
        result.set(index, [...(result.get(index) ?? []), p]);
    }
    return result;
}

/** A mark or an attachment between the transcript's turns. */
function PlacedRow({ placed, recordingId, onOpenAttachment }: { placed: Placed; recordingId: string; onOpenAttachment: (a: AttachmentDto) => void }) {
    if (placed.type === "mark") {
        const m = placed.item;
        return (
            <div className="flex items-start gap-2 rounded-lg border border-dashed border-warning/50 bg-warning/5 p-1.5 text-xs">
                <i className={`fa-solid ${markKindOf(m.kind).icon} mt-0.5 text-warning`} title={markKindOf(m.kind).label}></i>
                <span className="min-w-0 flex-1">{m.note ?? <span className="italic text-base-content/50">{m.pending ? "Analyzing in Plaud…" : "Marked"}</span>}</span>
                <span className="shrink-0 font-mono tabular-nums text-base-content/50">{formatClock(placed.offset)}</span>
            </div>
        );
    }
    const a = placed.item;
    return (
        <button
            className="flex w-full items-center gap-2 rounded-lg border border-dashed border-accent/40 bg-accent/5 p-1.5 text-left text-xs hover:bg-accent/10"
            onClick={() => onOpenAttachment(a)}
            title={`Open ${a.fileName}`}
        >
            <AttachmentThumb recordingId={recordingId} attachment={a} className="h-9 w-14 shrink-0 rounded text-base" />
            <i className="fa-solid fa-paperclip text-accent"></i>
            <span className="min-w-0 flex-1 truncate font-medium">{a.fileName}</span>
            <span className="shrink-0 font-mono tabular-nums text-base-content/50">{formatClock(placed.offset)}</span>
        </button>
    );
}

/** The raw transcript as stored after transcription: one block per speaker turn, with its start time. */
export function TranscriptView({ recordingId, attachments, marks, onOpenAttachment, onError }: Props) {
    const [segments, setSegments] = useState<TranscriptSegmentDto[] | null>(null);
    // The list re-renders on every poll; a ref keeps a new onError from refetching.
    const onErrorRef = useRef(onError);
    onErrorRef.current = onError;

    useEffect(() => {
        let cancelled = false;
        getTranscript(recordingId)
            .then((s) => { if (!cancelled) setSegments(s); })
            .catch(() => { if (!cancelled) { setSegments([]); onErrorRef.current("Could not load the transcript."); } });
        return () => { cancelled = true; };
    }, [recordingId]);

    if (segments === null) {
        return <div className="flex justify-center py-4"><span className="loading loading-spinner loading-sm"></span></div>;
    }
    if (segments.length === 0) {
        return <p className="text-base-content/50">No transcript stored.</p>;
    }

    const colorOf = new Map<string, (typeof speakerColors)[number]>();
    for (const s of segments) {
        if (s.speaker !== "UU" && !colorOf.has(s.speaker)) colorOf.set(s.speaker, speakerColors[colorOf.size % speakerColors.length]);
    }
    const unknown = { label: "text-base-content/60", bar: "border-base-content/20" };
    const placed = placeBySegment(segments, attachments, marks);

    const placedRows = (index: number) => placed.get(index)?.map((p) =>
        <PlacedRow key={p.item.id} placed={p} recordingId={recordingId} onOpenAttachment={onOpenAttachment} />);

    return (
        <div className="max-h-[32rem] space-y-3 overflow-y-auto pr-2">
            {placedRows(-1)}
            {segments.map((s, i) => {
                const color = colorOf.get(s.speaker) ?? unknown;
                return (
                    <div key={i} className="space-y-3">
                        <div className={`border-l-2 pl-3 ${color.bar}`}>
                            <div className="flex items-baseline gap-2 text-xs">
                                <span className={`font-semibold ${color.label}`}>{speakerName(s.speaker)}</span>
                                <span className="font-mono tabular-nums text-base-content/50">{formatClock(s.start)} – {formatClock(s.end)}</span>
                            </div>
                            <p className="mt-0.5 whitespace-pre-line">{s.text}</p>
                        </div>
                        {placedRows(i)}
                    </div>
                );
            })}
        </div>
    );
}
