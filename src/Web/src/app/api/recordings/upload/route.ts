import { env } from "@/env";

// Binary BFF passthrough for uploads: streams the multipart body to the backend unbuffered
// (server actions cap bodies at 1 MB, an hour of 128 kbit/s MP3 is ~60 MB).
export async function POST(request: Request) {
    const headers = new Headers();
    const contentType = request.headers.get("content-type");
    if (contentType) headers.set("content-type", contentType);
    const contentLength = request.headers.get("content-length");
    if (contentLength) headers.set("content-length", contentLength);
    const upstream = await fetch(`${env.API_URL}/api/recordings/upload`, {
        method: "POST",
        headers,
        body: request.body,
        cache: "no-store",
        // Required by Node's fetch for a streamed request body.
        duplex: "half",
    } as RequestInit & { duplex: "half" });
    const out = new Headers();
    const upstreamType = upstream.headers.get("content-type");
    if (upstreamType) out.set("content-type", upstreamType);
    return new Response(upstream.body, { status: upstream.status, headers: out });
}
