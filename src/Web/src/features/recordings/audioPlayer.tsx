"use client";

import { useRef, useState } from "react";
import { formatClock } from "./utils";
import { Waveform } from "./waveform";

// Shown until the backend has computed the peaks (a few seconds after sync).
const flatPeaks = new Array<number>(120).fill(0);

type Props = {
    recordingId: string;
    durationMs: number;
    peaks: number[] | null;
    onError: (message: string) => void;
};

/**
 * Click anywhere on the waveform to play from there. The audio is the locally stored copy,
 * streamed through the BFF route (app/api/recordings/[id]/audio); it is attached on first
 * play only, so the list does not load every recording up front.
 */
export function AudioPlayer({ recordingId, durationMs, peaks, onError }: Props) {
    const audioRef = useRef<HTMLAudioElement>(null);
    const pendingSeek = useRef<number | null>(null);
    const [src, setSrc] = useState<string | null>(null);
    const [loading, setLoading] = useState(false);
    const [playing, setPlaying] = useState(false);
    const [progress, setProgress] = useState(0);

    const duration = (() => {
        const d = audioRef.current?.duration;
        return d && Number.isFinite(d) ? d : durationMs / 1000;
    })();

    const loadSource = (resumeAt: number) => {
        pendingSeek.current = resumeAt;
        setLoading(true);
        setSrc(`/api/recordings/${encodeURIComponent(recordingId)}/audio`);
    };

    /** Plays from `ratio` (0..1), or from the current position. */
    const playFrom = async (ratio?: number) => {
        const audio = audioRef.current;
        if (!audio) return;
        if (!src) {
            loadSource(ratio ?? progress);
            return;
        }
        if (ratio !== undefined) {
            audio.currentTime = ratio * duration;
            setProgress(ratio);
        }
        await audio.play().catch(() => {});
    };

    const toggle = () => {
        if (playing) audioRef.current?.pause();
        else playFrom();
    };

    return (
        <div className="flex w-full items-center gap-3">
            <button
                className="btn btn-circle btn-primary shrink-0 border-0 bg-gradient-brand shadow-md shadow-primary/30"
                onClick={toggle}
                disabled={loading}
                aria-label={playing ? "Pause" : "Play"}
            >
                {loading
                    ? <span className="loading loading-spinner loading-xs"></span>
                    : <i className={`fa-solid ${playing ? "fa-pause" : "fa-play"}`}></i>}
            </button>

            <div className="min-w-0 flex-1 pt-1">
                <Waveform
                    peaks={peaks ?? flatPeaks}
                    progress={progress}
                    durationSeconds={duration}
                    onSeek={(ratio) => playFrom(ratio)}
                    height={44}
                />
            </div>

            <span className="shrink-0 font-mono text-xs text-base-content/70 tabular-nums">
                {formatClock(progress * duration)} / {formatClock(duration)}
            </span>

            <audio
                ref={audioRef}
                src={src ?? undefined}
                preload="metadata"
                onLoadedMetadata={(e) => {
                    setLoading(false);
                    const audio = e.currentTarget;
                    if (pendingSeek.current !== null) {
                        audio.currentTime = pendingSeek.current * (Number.isFinite(audio.duration) ? audio.duration : duration);
                        pendingSeek.current = null;
                        audio.play().catch(() => {});
                    }
                }}
                onTimeUpdate={(e) => {
                    const audio = e.currentTarget;
                    const d = Number.isFinite(audio.duration) ? audio.duration : duration;
                    if (d > 0) setProgress(Math.min(1, audio.currentTime / d));
                }}
                onPlay={() => setPlaying(true)}
                onPause={() => setPlaying(false)}
                onEnded={() => setPlaying(false)}
                onError={() => {
                    setPlaying(false);
                    setLoading(false);
                    if (src) onError("Playback failed - the audio could not be loaded.");
                }}
            />
        </div>
    );
}
