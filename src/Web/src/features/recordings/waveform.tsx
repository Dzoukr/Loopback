"use client";

import { useEffect, useId, useMemo, useRef, useState } from "react";
import type { KeyboardEvent, PointerEvent } from "react";
import { formatClock } from "./utils";

type Props = {
    /** Envelope values in [0, 1], any length (resampled to the available width). */
    peaks: number[];
    /** Playback position in [0, 1]. */
    progress: number;
    durationSeconds: number;
    /** Called with the requested position in [0, 1]. */
    onSeek: (ratio: number) => void;
    height?: number;
};

const BAR_PITCH_PX = 5; // bar + gap
const BAR_GAP_PX = 2;
const MIN_BARS = 24;
const MAX_BARS = 240;
const MIN_BAR_PX = 1.5;
const KEY_STEP_SECONDS = 5;
const KEY_STEP_SECONDS_LARGE = 30;

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

/** Downsamples `peaks` to `count` buckets, keeping each bucket's loudest value. */
function resample(peaks: number[], count: number): number[] {
    if (peaks.length <= count) return peaks;
    const step = peaks.length / count;
    return Array.from({ length: count }, (_, i) => {
        const from = Math.floor(i * step);
        const to = Math.max(from + 1, Math.floor((i + 1) * step));
        return Math.max(...peaks.slice(from, to));
    });
}

/** One SVG path with a bar per value, mirrored around the horizontal centre line. */
function barsPath(values: number[], width: number, height: number): string {
    if (values.length === 0 || width <= 0) return "";
    const pitch = width / values.length;
    const barWidth = Math.max(1, pitch - BAR_GAP_PX);
    const mid = height / 2;
    return values
        .map((v, i) => {
            const h = Math.max(MIN_BAR_PX, clamp01(v) * height);
            const x = i * pitch + (pitch - barWidth) / 2;
            return `M${x.toFixed(2)} ${(mid - h / 2).toFixed(2)}h${barWidth.toFixed(2)}v${h.toFixed(2)}h${(-barWidth).toFixed(2)}z`;
        })
        .join("");
}

/**
 * Seekable waveform: bars drawn as a single SVG path, the played part revealed by a clip
 * rectangle (so a progress tick only moves one attribute). Click or drag to seek, arrow keys
 * step 5 s (Shift: 30 s), hovering shows the time under the pointer.
 */
export function Waveform({ peaks, progress, durationSeconds, onSeek, height = 56 }: Props) {
    const rootRef = useRef<HTMLDivElement>(null);
    const [width, setWidth] = useState(0);
    const [hover, setHover] = useState<number | null>(null);
    const [scrubbing, setScrubbing] = useState(false);
    const ids = useId();
    const clipId = `${ids}-played`;
    const gradientId = `${ids}-fill`;

    useEffect(() => {
        const el = rootRef.current;
        if (!el) return;
        setWidth(el.clientWidth);
        const observer = new ResizeObserver(([entry]) => setWidth(entry.contentRect.width));
        observer.observe(el);
        return () => observer.disconnect();
    }, []);

    const path = useMemo(() => {
        const count = Math.min(MAX_BARS, Math.max(MIN_BARS, Math.floor(width / BAR_PITCH_PX)));
        return barsPath(resample(peaks, count), width, height);
    }, [peaks, width, height]);

    const duration = Number.isFinite(durationSeconds) && durationSeconds > 0 ? durationSeconds : 0;
    const position = clamp01(progress);

    const ratioAt = (clientX: number) => {
        const box = rootRef.current?.getBoundingClientRect();
        return box && box.width > 0 ? clamp01((clientX - box.left) / box.width) : 0;
    };

    const handlePointerDown = (e: PointerEvent<HTMLDivElement>) => {
        if (e.button !== 0) return;
        e.currentTarget.setPointerCapture(e.pointerId);
        setScrubbing(true);
        onSeek(ratioAt(e.clientX));
    };

    const handlePointerMove = (e: PointerEvent<HTMLDivElement>) => {
        const ratio = ratioAt(e.clientX);
        if (e.pointerType === "mouse") setHover(ratio);
        if (scrubbing) onSeek(ratio);
    };

    const stopScrubbing = () => setScrubbing(false);

    const handleKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
        const stepSeconds = e.shiftKey ? KEY_STEP_SECONDS_LARGE : KEY_STEP_SECONDS;
        const step = duration > 0 ? stepSeconds / duration : 0.01;
        const target =
            e.key === "ArrowLeft" ? position - step
            : e.key === "ArrowRight" ? position + step
            : e.key === "Home" ? 0
            : e.key === "End" ? 1
            : null;
        if (target === null) return;
        e.preventDefault();
        onSeek(clamp01(target));
    };

    const playedX = position * width;

    return (
        <div
            ref={rootRef}
            className="relative w-full cursor-pointer touch-none select-none rounded-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/50"
            style={{ height }}
            role="slider"
            tabIndex={0}
            aria-label="Seek"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={Math.round(position * 100)}
            aria-valuetext={`${formatClock(position * duration)} of ${formatClock(duration)}`}
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={stopScrubbing}
            onPointerCancel={stopScrubbing}
            onPointerLeave={() => setHover(null)}
            onKeyDown={handleKeyDown}
        >
            {width > 0 && (
                <svg className="block" width={width} height={height} aria-hidden="true">
                    <defs>
                        <linearGradient id={gradientId} x1="0" x2="1" y1="0" y2="0">
                            <stop offset="0" style={{ stopColor: "var(--color-primary)" }} />
                            <stop offset="1" style={{ stopColor: "var(--color-secondary)" }} />
                        </linearGradient>
                        <clipPath id={clipId}>
                            <rect x={0} y={0} width={playedX} height={height} />
                        </clipPath>
                    </defs>

                    <path d={path} className="fill-base-content/30" />
                    <path d={path} fill={`url(#${gradientId})`} clipPath={`url(#${clipId})`} />

                    {hover !== null && (
                        <rect x={Math.round(hover * width)} y={0} width={1} height={height} className="fill-base-content/50" />
                    )}
                    {position > 0 && position < 1 && (
                        <rect x={playedX - 1} y={0} width={2} height={height} className="fill-primary" />
                    )}
                </svg>
            )}

            {hover !== null && duration > 0 && (
                <span
                    className="pointer-events-none absolute -top-7 z-10 -translate-x-1/2 rounded bg-base-content px-1.5 py-0.5 font-mono text-[11px] text-base-100 shadow-md"
                    style={{ left: `${hover * 100}%` }}
                    aria-hidden="true"
                >
                    {formatClock(hover * duration)}
                </span>
            )}
        </div>
    );
}
