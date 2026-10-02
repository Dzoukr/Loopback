"use client";

// Renders the `content` of a result file. Its shape comes from the workflow's JSON schema, so
// the view is generic: strings -> paragraphs, string lists -> bullet lists (a leading "[Topic]"
// becomes a badge), nested objects -> labelled sub-sections.

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

export function ResultView({ result }: { result: string }) {
    let content: Json;
    try {
        const envelope = JSON.parse(result) as { content?: Json };
        content = envelope.content ?? null;
    } catch {
        return <p className="text-sm text-error">The stored result is not valid JSON.</p>;
    }
    if (content === null || typeof content !== "object" || Array.isArray(content)) {
        return <Value value={content} />;
    }
    return <Fields value={content} />;
}
