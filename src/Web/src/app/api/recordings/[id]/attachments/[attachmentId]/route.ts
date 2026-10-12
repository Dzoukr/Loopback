import { env } from "@/env";

// Binary BFF passthrough for an attachment's file. The backend decides inline vs. download
// (Content-Disposition) and sends nosniff; both are forwarded as they are. Range is forwarded
// too, so audio attachments (Plaud voice memos) can seek.
const passedHeaders = [
    "content-type", "content-length", "content-range", "accept-ranges", "content-disposition",
    "x-content-type-options", "cache-control", "last-modified", "etag",
];

export async function GET(request: Request, { params }: { params: Promise<{ id: string; attachmentId: string }> }) {
    const { id, attachmentId } = await params;
    const download = new URL(request.url).searchParams.has("download") ? "?download=1" : "";
    const headers = new Headers();
    const range = request.headers.get("range");
    if (range) headers.set("range", range);
    const upstream = await fetch(
        `${env.API_URL}/api/recordings/${encodeURIComponent(id)}/attachments/${encodeURIComponent(attachmentId)}${download}`,
        { headers, cache: "no-store" });
    const out = new Headers();
    for (const h of passedHeaders) {
        const value = upstream.headers.get(h);
        if (value) out.set(h, value);
    }
    return new Response(upstream.body, { status: upstream.status, headers: out });
}
