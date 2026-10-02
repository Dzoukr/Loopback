import type { Metadata } from "next";
import { RecordingsContent } from "@/features/recordings/recordingsContent";

export const metadata: Metadata = {
    title: "Recordings — Loopback",
};

export default function RecordingsPage() {
    return <RecordingsContent />;
}
