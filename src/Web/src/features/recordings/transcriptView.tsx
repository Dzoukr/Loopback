"use client";

import { useEffect, useRef, useState } from "react";
import type { TranscriptSegmentDto } from "@/lib/generated/api-client";
import { getTranscript } from "./actions";
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
    onError: (message: string) => void;
};

/** The raw transcript as stored after transcription: one block per speaker turn, with its start time. */
export function TranscriptView({ recordingId, onError }: Props) {
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

    return (
        <div className="max-h-[32rem] space-y-3 overflow-y-auto pr-2">
            {segments.map((s, i) => {
                const color = colorOf.get(s.speaker) ?? unknown;
                return (
                    <div key={i} className={`border-l-2 pl-3 ${color.bar}`}>
                        <div className="flex items-baseline gap-2 text-xs">
                            <span className={`font-semibold ${color.label}`}>{speakerName(s.speaker)}</span>
                            <span className="font-mono tabular-nums text-base-content/50">{formatClock(s.start)} – {formatClock(s.end)}</span>
                        </div>
                        <p className="mt-0.5 whitespace-pre-line">{s.text}</p>
                    </div>
                );
            })}
        </div>
    );
}
