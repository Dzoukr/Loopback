"use client";

import { markKindOf } from "./attachments";
import { formatClock } from "./utils";

// Renders the result of a processed recording. The Default workflow's shape (summary, topics with key
// points, decisions, open questions, action items - docs/attachments-processing.md) gets its own layout,
// with the envelope's notes (marks) and attachments at the end. Any other workflow's `content` is
// rendered generically: strings -> paragraphs, string lists -> bullet lists (a leading "[Topic]" becomes
// a badge), nested objects -> labelled sub-sections.

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };

type Topic = { id: string; title: string; points: string[] };
type ActionItem = { owner: string | null; text: string; due: string | null };
type Structured = { summary: string; topics: Topic[]; decisions: string[]; openQuestions: string[]; actionItems: ActionItem[] };

type ExportMark = { id: string; kind: string; topic: string | null; offsetSeconds: number; note: string | null; context: string };
type ExportAttachment = { id: string; fileName: string; kind: string; topic: string | null; offsetSeconds: number | null; note: string | null };

type Envelope = { content?: Json; marks?: ExportMark[]; attachments?: ExportAttachment[] };

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

function Fields({ value }: { value: { [key: string]: Json } }) {
    return (
        <div className="space-y-4">
            {Object.entries(value).map(([key, v]) => (
                <div key={key}>
                    <h4 className="mb-1 text-xs font-semibold uppercase tracking-wider text-primary">{label(key)}</h4>
                    <Value value={v} />
                </div>
            ))}
        </div>
    );
}

function Section({ title, icon, children }: { title: string; icon: string; children: React.ReactNode }) {
    return (
        <section>
            <h4 className="mb-1.5 flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-primary">
                <i className={`fa-solid ${icon}`}></i>{title}
            </h4>
            {children}
        </section>
    );
}

const none = <p className="text-sm text-base-content/50">None</p>;

function Bullets({ items }: { items: string[] }) {
    return items.length === 0 ? none : <ul className="list-disc space-y-1 pl-5">{items.map((t, i) => <li key={i}>{t}</li>)}</ul>;
}

function isStructured(content: Json): content is Structured & { [key: string]: Json } {
    return content !== null && typeof content === "object" && !Array.isArray(content)
        && Array.isArray(content.topics) && Array.isArray(content.decisions) && Array.isArray(content.actionItems)
        && content.topics.every((t) => t !== null && typeof t === "object" && !Array.isArray(t) && Array.isArray(t.points)
            && t.points.every((p) => typeof p === "string"));
}

/** Notes (marks) and attachments in capture order: time in the call, then attachments from around it. */
function AttachmentsAndNotes({ marks, attachments, onSeek, onOpenAttachment }: {
    marks: ExportMark[];
    attachments: ExportAttachment[];
    onSeek: (seconds: number) => void;
    onOpenAttachment: (fileName: string) => void;
}) {
    type Row = { key: string; time: number | null; render: React.ReactNode };
    const rows: Row[] = [
        ...marks.map((m) => {
            const kind = markKindOf(m.kind);
            // A highlight without text shows what was said around it.
            const text = m.note ?? (m.context ? `"${m.context}"` : null);
            return {
                key: m.id,
                time: m.offsetSeconds,
                render: (
                    <>
                        <i className={`fa-solid ${kind.icon} mt-1 text-warning`} title={kind.label}></i>
                        <span className="min-w-0 flex-1">
                            <span className="font-medium">{kind.label}</span>
                            {m.topic && <span className="italic text-base-content/60"> ({m.topic})</span>}
                            {text && <> - {text}</>}
                        </span>
                    </>
                ),
            };
        }),
        ...attachments.map((a) => ({
            key: a.id,
            time: a.offsetSeconds,
            render: (
                <>
                    <i className="fa-solid fa-paperclip mt-1 text-accent"></i>
                    <span className="min-w-0 flex-1">
                        <button className="link link-hover font-medium" onClick={() => onOpenAttachment(a.fileName)}>{a.fileName}</button>
                        {a.topic && <span className="italic text-base-content/60"> ({a.topic})</span>}
                        {a.note && <> - {a.note}</>}
                    </span>
                </>
            ),
        })),
    ].sort((x, y) => (x.time ?? Number.MAX_VALUE) - (y.time ?? Number.MAX_VALUE));
    if (rows.length === 0) return none;
    return (
        <ul className="space-y-1.5">
            {rows.map((r) => (
                <li key={r.key} className="flex items-start gap-2">
                    {r.render}
                    {r.time !== null
                        ? <button className="badge badge-sm badge-ghost shrink-0 cursor-pointer rounded-full font-mono" onClick={() => onSeek(r.time ?? 0)} title="Jump to this moment">{formatClock(r.time)}</button>
                        : <span className="badge badge-sm badge-ghost shrink-0 rounded-full">around the call</span>}
                </li>
            ))}
        </ul>
    );
}

function StructuredResult({ content, envelope, onSeek, onOpenAttachment }: { content: Structured; envelope: Envelope } & Omit<Props, "result">) {
    // Action items grouped by owner, in order of first appearance.
    const owners: { owner: string | null; items: ActionItem[] }[] = [];
    for (const item of content.actionItems) {
        const group = owners.find((g) => g.owner === item.owner);
        if (group) group.items.push(item);
        else owners.push({ owner: item.owner, items: [item] });
    }
    return (
        <div className="space-y-5">
            <Section title="Summary" icon="fa-align-left"><p className="whitespace-pre-line">{content.summary}</p></Section>
            <Section title="Key points" icon="fa-list">
                {content.topics.length === 0 ? none : (
                    <div className="space-y-3">
                        {content.topics.map((t) => (
                            <div key={t.id}>
                                <div className="font-semibold">{t.title}</div>
                                <Bullets items={t.points} />
                            </div>
                        ))}
                    </div>
                )}
            </Section>
            <Section title="Decisions" icon="fa-circle-check"><Bullets items={content.decisions} /></Section>
            <Section title="Open questions" icon="fa-circle-question"><Bullets items={content.openQuestions ?? []} /></Section>
            <Section title="Action items" icon="fa-list-check">
                {owners.length === 0 ? none : (
                    <div className="space-y-3">
                        {owners.map((g) => (
                            <div key={g.owner ?? ""}>
                                <div className="font-semibold">{g.owner ?? "Unassigned"}</div>
                                <ul className="space-y-1">
                                    {g.items.map((item, i) => (
                                        <li key={i} className="flex items-start gap-2">
                                            <i className="fa-regular fa-square mt-1 text-base-content/40"></i>
                                            <span>
                                                {item.text}
                                                {item.due && <span className="badge badge-sm badge-soft badge-info ml-1.5 rounded-full align-middle"><i className="fa-regular fa-calendar mr-1"></i>{item.due}</span>}
                                            </span>
                                        </li>
                                    ))}
                                </ul>
                            </div>
                        ))}
                    </div>
                )}
            </Section>
            <Section title="Attachments & notes" icon="fa-paperclip">
                <AttachmentsAndNotes marks={envelope.marks ?? []} attachments={envelope.attachments ?? []} onSeek={onSeek} onOpenAttachment={onOpenAttachment} />
            </Section>
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
    let envelope: Envelope;
    try {
        envelope = JSON.parse(result) as Envelope;
    } catch {
        return <p className="text-sm text-error">The stored result is not valid JSON.</p>;
    }
    const content = envelope.content ?? null;
    if (isStructured(content)) return <StructuredResult content={content} envelope={envelope} onSeek={onSeek} onOpenAttachment={onOpenAttachment} />;
    if (content === null || typeof content !== "object" || Array.isArray(content)) return <Value value={content} />;
    return <Fields value={content} />;
}
