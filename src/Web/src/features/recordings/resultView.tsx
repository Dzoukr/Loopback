"use client";

import { createContext, useContext } from "react";
import { markKindOf } from "./attachments";
import { formatClock } from "./utils";

// Renders the `content` of a result file. Its shape comes from the workflow's JSON schema, so
// the view is generic: strings -> paragraphs, string lists -> bullet lists (a leading "[Topic]"
// becomes a badge), nested objects -> labelled sub-sections. Links to the envelope's marks and
// attachments (markIds / attachmentIds, docs/attachments-processing.md) become chips, topic spans
// time ranges - both jump the player.

type ExportMark = { id: string; kind: string; offsetSeconds: number; note: string | null };
type ExportAttachment = { id: string; fileName: string; offsetSeconds: number | null };

type Links = {
    marks: Map<string, ExportMark>;
    attachments: Map<string, ExportAttachment>;
    topics: Map<string, string>;
    onSeek: (seconds: number) => void;
    onOpenAttachment: (fileName: string) => void;
};

const LinksContext = createContext<Links | null>(null);

function Chips({ ids, type }: { ids: Json[]; type: "mark" | "attachment" }) {
    const links = useContext(LinksContext);
    if (!links || ids.length === 0) return null;
    return (
        <span className="ml-1 inline-flex flex-wrap gap-1 align-middle">
            {ids.map((raw) => {
                const id = String(raw);
                if (type === "mark") {
                    const m = links.marks.get(id);
                    if (!m) return null;
                    const kind = markKindOf(m.kind);
                    return (
                        <button
                            key={id}
                            className="badge badge-sm badge-soft badge-warning cursor-pointer gap-1 rounded-full"
                            onClick={() => links.onSeek(m.offsetSeconds)}
                            title={`${kind.label}${m.note ? `: ${m.note}` : ""} - jump to ${formatClock(m.offsetSeconds)}`}
                        >
                            <i className={`fa-solid ${kind.icon}`}></i>{formatClock(m.offsetSeconds)}
                        </button>
                    );
                }
                const a = links.attachments.get(id);
                if (!a) return null;
                return (
                    <button
                        key={id}
                        className="badge badge-sm badge-soft badge-accent max-w-48 cursor-pointer gap-1 rounded-full"
                        onClick={() => links.onOpenAttachment(a.fileName)}
                        title={`Open ${a.fileName}`}
                    >
                        <i className="fa-solid fa-paperclip"></i><span className="truncate">{a.fileName}</span>
                    </button>
                );
            })}
        </span>
    );
}

function Spans({ spans }: { spans: Json[] }) {
    const links = useContext(LinksContext);
    return (
        <span className="inline-flex flex-wrap gap-1">
            {spans.map((span, i) => {
                if (span === null || typeof span !== "object" || Array.isArray(span)) return null;
                const start = Number(span.startSeconds), end = Number(span.endSeconds);
                if (!Number.isFinite(start)) return null;
                return (
                    <button key={i} className="badge badge-sm badge-ghost cursor-pointer rounded-full font-mono" onClick={() => links?.onSeek(start)} title="Jump to this part">
                        {formatClock(start)}{Number.isFinite(end) ? ` - ${formatClock(end)}` : ""}
                    </button>
                );
            })}
        </span>
    );
}

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };

/** "keyPoints" -> "Key points", "action_items" -> "Action items" */
function label(key: string): string {
    const words = key.replace(/[_-]+/g, " ").replace(/([a-z0-9])([A-Z])/g, "$1 $2").trim().toLowerCase();
    return words.charAt(0).toUpperCase() + words.slice(1);
}

/** "[Pricing] [John] Text" -> tags ["Pricing", "John"], text "Text" */
function splitTags(text: string): { tags: string[]; text: string } {
    const tags: string[] = [];
    let rest = text.trim();
    let match: RegExpMatchArray | null;
    while ((match = rest.match(/^\[([^\]]{1,40})\]\s*/))) {
        tags.push(match[1]);
        rest = rest.slice(match[0].length);
    }
    return { tags, text: rest };
}

function Text({ value }: { value: string }) {
    const { tags, text } = splitTags(value);
    return (
        <span>
            {tags.map((t) => <span key={t} className="badge badge-sm badge-soft badge-primary mr-1 rounded-full align-middle">{t}</span>)}
            {text}
        </span>
    );
}

function Value({ value }: { value: Json }) {
    if (value === null || value === undefined) return <span className="text-base-content/50">—</span>;
    if (typeof value === "string") return <p className="whitespace-pre-line"><Text value={value} /></p>;
    if (typeof value === "number" || typeof value === "boolean") return <span>{String(value)}</span>;
    if (Array.isArray(value)) {
        if (value.length === 0) return <p className="text-sm text-base-content/50">None</p>;
        return (
            <ul className="list-disc space-y-1 pl-5">
                {value.map((item, i) => (
                    <li key={i}>{typeof item === "string" ? <Text value={item} /> : <Value value={item} />}</li>
                ))}
            </ul>
        );
    }
    return <Fields value={value} />;
}

/** Keys that are links or internal ids - rendered inline (chips) or not at all, never as a section. */
const linkKeys = new Set(["id", "markIds", "attachmentIds", "topicId", "spans"]);

function Fields({ value }: { value: { [key: string]: Json } }) {
    const links = useContext(LinksContext);
    const markIds = Array.isArray(value.markIds) ? value.markIds : [];
    const attachmentIds = Array.isArray(value.attachmentIds) ? value.attachmentIds : [];
    const topicTitle = typeof value.topicId === "string" ? links?.topics.get(value.topicId) : undefined;
    // A linked entry ({ text, markIds, attachmentIds, ... }) reads as one line: the text, then its chips.
    const text = typeof value.text === "string" ? value.text : null;
    const rest = Object.entries(value).filter(([key]) => !linkKeys.has(key) && !(key === "text" && text !== null));
    return (
        <div className="space-y-4">
            {(text !== null || markIds.length > 0 || attachmentIds.length > 0 || topicTitle) && (
                <p className="whitespace-pre-line">
                    {text !== null && <Text value={text} />}
                    <Chips ids={markIds} type="mark" />
                    <Chips ids={attachmentIds} type="attachment" />
                    {topicTitle && <span className="badge badge-sm badge-ghost ml-1 rounded-full align-middle">{topicTitle}</span>}
                </p>
            )}
            {Array.isArray(value.spans) && value.spans.length > 0 && <Spans spans={value.spans} />}
            {rest.map(([key, v]) => (
                <div key={key}>
                    <h4 className="mb-1 text-xs font-semibold uppercase tracking-wider text-primary">{label(key)}</h4>
                    <Value value={v} />
                </div>
            ))}
        </div>
    );
}

type Props = {
    result: string;
    onSeek: (seconds: number) => void;
    /** Opens the recording's attachment with this file name (the export has no Loopback ids). */
    onOpenAttachment: (fileName: string) => void;
};

export function ResultView({ result, onSeek, onOpenAttachment }: Props) {
    let content: Json;
    let links: Links;
    try {
        const envelope = JSON.parse(result) as { content?: Json; marks?: ExportMark[]; attachments?: ExportAttachment[] };
        content = envelope.content ?? null;
        const topics = new Map<string, string>();
        if (content && typeof content === "object" && !Array.isArray(content) && Array.isArray(content.topics)) {
            for (const t of content.topics) {
                if (t && typeof t === "object" && !Array.isArray(t) && typeof t.id === "string" && typeof t.title === "string") topics.set(t.id, t.title);
            }
        }
        links = {
            marks: new Map((envelope.marks ?? []).map((m) => [m.id, m])),
            attachments: new Map((envelope.attachments ?? []).map((a) => [a.id, a])),
            topics,
            onSeek,
            onOpenAttachment,
        };
    } catch {
        return <p className="text-sm text-error">The stored result is not valid JSON.</p>;
    }
    return (
        <LinksContext.Provider value={links}>
            {content === null || typeof content !== "object" || Array.isArray(content) ? <Value value={content} /> : <Fields value={content} />}
        </LinksContext.Provider>
    );
}
