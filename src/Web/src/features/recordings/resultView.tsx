"use client";

import { markKindOf } from "./attachments";
import { formatClock } from "./utils";

// Renders the result of a processed recording - any workflow's shape, by convention, in the order of
// the result's own fields; nothing here knows a particular workflow:
// - top-level fields -> sections (the field name is the heading);
// - strings -> paragraphs, string lists -> bullet lists (a leading "[Topic]" becomes a badge);
// - lists of tasks (objects with "text" and an "owner" and / or a "due") -> a checklist, grouped by owner
//   when there are owners, with the due date as a badge;
// - lists of titled objects (with "title") -> one block per item: the title, its text fields as
//   paragraphs, its string lists as bullets, anything else labelled;
// - other objects -> labelled sub-sections.
// "id" fields are left out - they only link notes / attachments to a topic. The envelope's notes (marks)
// and attachments follow at the end, when there are any.

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };
type JsonObject = { [key: string]: Json };

/** offsetSeconds and context are null for a note without a time. */
type ExportMark = { id: string; kind: string; topic: string | null; offsetSeconds: number | null; note: string | null; context: string | null };
type ExportAttachment = { id: string; fileName: string; kind: string; topic: string | null; offsetSeconds: number | null; note: string | null };

type Envelope = { content?: Json; marks?: ExportMark[]; attachments?: ExportAttachment[] };

type Task = { text: string; owner: string | null; due: string | null };

const isObject = (value: Json | undefined): value is JsonObject =>
    value !== null && value !== undefined && typeof value === "object" && !Array.isArray(value);

const isStringList = (value: Json): value is string[] => Array.isArray(value) && value.every((v) => typeof v === "string");

/** Fields worth showing - without "id". */
const visible = (value: JsonObject) => Object.entries(value).filter(([key]) => key !== "id");

/** "keyPoints" -> "Key points", "action_items" -> "Action items" */
function label(key: string): string {
    const words = key.replace(/[_-]+/g, " ").replace(/([a-z0-9])([A-Z])/g, "$1 $2").trim().toLowerCase();
    return words.charAt(0).toUpperCase() + words.slice(1);
}

/** An icon for a section heading, guessed from the field name. */
function iconOf(key: string): string {
    const k = key.toLowerCase();
    if (k.includes("summary") || k.includes("focus")) return "fa-align-left";
    if (k.includes("question") || k.includes("challenge")) return "fa-circle-question";
    if (k.includes("decision")) return "fa-circle-check";
    if (k.includes("action") || k.includes("task") || k.includes("step")) return "fa-list-check";
    if (k.includes("idea")) return "fa-lightbulb";
    return "fa-list";
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

const none = <p className="text-sm text-base-content/50">None</p>;

function Bullets({ items }: { items: string[] }) {
    return items.length === 0 ? none : <ul className="list-disc space-y-1 pl-5">{items.map((t, i) => <li key={i}><Text value={t} /></li>)}</ul>;
}

/** A list of tasks: objects with a text and an owner and / or a due date (nothing else but an id). */
function asTasks(items: Json[]): Task[] | null {
    const tasks: Task[] = [];
    for (const item of items) {
        if (!isObject(item) || typeof item.text !== "string" || !("owner" in item || "due" in item)) return null;
        if (Object.keys(item).some((k) => !["id", "text", "owner", "due"].includes(k))) return null;
        tasks.push({
            text: item.text,
            owner: typeof item.owner === "string" ? item.owner : null,
            due: typeof item.due === "string" ? item.due : null,
        });
    }
    return tasks;
}

function TaskItems({ tasks }: { tasks: Task[] }) {
    return (
        <ul className="space-y-1">
            {tasks.map((t, i) => (
                <li key={i} className="flex items-start gap-2">
                    <i className="fa-regular fa-square mt-1 text-base-content/40"></i>
                    <span>
                        <Text value={t.text} />
                        {t.due && <span className="badge badge-sm badge-soft badge-info ml-1.5 rounded-full align-middle"><i className="fa-regular fa-calendar mr-1"></i>{t.due}</span>}
                    </span>
                </li>
            ))}
        </ul>
    );
}

/** Tasks grouped by owner, in order of first appearance - flat when nobody owns any. */
function Checklist({ tasks }: { tasks: Task[] }) {
    if (tasks.every((t) => t.owner === null)) return <TaskItems tasks={tasks} />;
    const groups: { owner: string | null; tasks: Task[] }[] = [];
    for (const task of tasks) {
        const group = groups.find((g) => g.owner === task.owner);
        if (group) group.tasks.push(task);
        else groups.push({ owner: task.owner, tasks: [task] });
    }
    return (
        <div className="space-y-3">
            {groups.map((g) => (
                <div key={g.owner ?? ""}>
                    <div className="font-semibold">{g.owner ?? "Unassigned"}</div>
                    <TaskItems tasks={g.tasks} />
                </div>
            ))}
        </div>
    );
}

/** One item of a list of titled objects: the title, then its other fields. */
function TitledItem({ item }: { item: JsonObject & { title: string } }) {
    const fields = visible(item).filter(([key]) => key !== "title");
    let paragraphs = 0;
    return (
        <div>
            <div className="font-semibold"><Text value={item.title} /></div>
            <div className="space-y-1">
                {fields.map(([key, v]) => {
                    if (typeof v === "string") {
                        if (v.trim() === "") return null;
                        // The first text reads as the item's lead, the next ones as its detail.
                        const muted = paragraphs++ > 0;
                        return <p key={key} className={`whitespace-pre-line ${muted ? "text-sm text-base-content/70" : ""}`}><Text value={v} /></p>;
                    }
                    if (isStringList(v)) return v.length === 0 ? null : <Bullets key={key} items={v} />;
                    return (
                        <div key={key}>
                            <div className="text-xs font-semibold text-base-content/60">{label(key)}</div>
                            <Value value={v} />
                        </div>
                    );
                })}
            </div>
        </div>
    );
}

function Value({ value }: { value: Json }) {
    if (value === null || value === undefined) return <span className="text-base-content/50">—</span>;
    if (typeof value === "string") return <p className="whitespace-pre-line"><Text value={value} /></p>;
    if (typeof value === "number" || typeof value === "boolean") return <span>{String(value)}</span>;
    if (Array.isArray(value)) {
        if (value.length === 0) return none;
        if (isStringList(value)) return <Bullets items={value} />;
        const tasks = asTasks(value);
        if (tasks) return <Checklist tasks={tasks} />;
        if (value.every((v) => isObject(v) && typeof v.title === "string")) {
            return (
                <div className="space-y-3">
                    {value.map((v, i) => <TitledItem key={i} item={v as JsonObject & { title: string }} />)}
                </div>
            );
        }
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

/** A nested object: its fields under small labels. */
function Fields({ value }: { value: JsonObject }) {
    return (
        <div className="space-y-2">
            {visible(value).map(([key, v]) => (
                <div key={key}>
                    <div className="text-xs font-semibold text-base-content/60">{label(key)}</div>
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
    const marks = envelope.marks ?? [];
    const attachments = envelope.attachments ?? [];
    return (
        <div className="space-y-5">
            {isObject(content)
                ? visible(content).map(([key, v]) => <Section key={key} title={label(key)} icon={iconOf(key)}><Value value={v} /></Section>)
                : <Value value={content} />}
            {(marks.length > 0 || attachments.length > 0) && (
                <Section title="Attachments & notes" icon="fa-paperclip">
                    <AttachmentsAndNotes marks={marks} attachments={attachments} onSeek={onSeek} onOpenAttachment={onOpenAttachment} />
                </Section>
            )}
        </div>
    );
}
