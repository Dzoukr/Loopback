import { env } from "@/env";

// Binary BFF passthrough for the player: streams the locally stored audio from the backend,
// forwarding Range so the browser can seek.
const passedHeaders = ["content-type", "content-length", "content-range", "accept-ranges", "last-modified", "etag"];

export async function GET(request: Request, { params }: { params: Promise<{ id: string }> }) {
    const { id } = await params;
    const headers = new Headers();
    const range = request.headers.get("range");
    if (range) headers.set("range", range);
    const upstream = await fetch(`${env.API_URL}/api/recordings/${encodeURIComponent(id)}/audio`, { headers, cache: "no-store" });
    const out = new Headers();
    for (const h of passedHeaders) {
        const value = upstream.headers.get(h);
        if (value) out.set(h, value);
    }
    return new Response(upstream.body, { status: upstream.status, headers: out });
}
