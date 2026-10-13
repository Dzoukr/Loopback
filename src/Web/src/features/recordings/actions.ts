"use server";

import { createApiClient } from "@/api";
import type { WorkflowDto, RecordingDto, SyncStatusDto, TranscriptSegmentDto } from "@/lib/generated/api-client";

// Every backend call goes through these server actions (BFF) - components never call the backend.
// Binary content goes through route handlers instead: the audio (app/api/recordings/[id]/audio) and
// attachment files (app/api/recordings/[id]/attachments).

export async function getRecordings(): Promise<RecordingDto[]> {
    const response = await createApiClient().getRecordings();
    return response.data;
}

export async function getWorkflows(): Promise<WorkflowDto[]> {
    const response = await createApiClient().getWorkflows();
    return response.data;
}

export async function getSyncStatus(): Promise<SyncStatusDto> {
    const response = await createApiClient().getSyncStatus();
    return response.data;
}

/** Speaker segments of the stored transcript (empty until transcription finished). */
export async function getTranscript(recordingId: string): Promise<TranscriptSegmentDto[]> {
    const response = await createApiClient().getTranscript(encodeURIComponent(recordingId));
    return response.data;
}

export async function processRecording(recordingId: string, workflow: string): Promise<void> {
    await createApiClient().processRecording({ recordingId, workflow });
}

export async function retryRecording(recordingId: string): Promise<void> {
    await createApiClient().retryRecording({ recordingId });
}

export async function syncNow(): Promise<void> {
    await createApiClient().syncNow();
}

/** Runs the workflow again on the stored transcript (no new transcription). */
export async function reprocessRecording(recordingId: string, workflow: string): Promise<void> {
    await createApiClient().reprocessRecording({ recordingId, workflow });
}

/** Hides the recording in Loopback (Plaud keeps the file). */
export async function deleteRecording(recordingId: string): Promise<void> {
    await createApiClient().deleteRecording({ recordingId });
}

/** Deletes every processed recording, like deleteRecording does one; returns how many. */
export async function deleteProcessed(): Promise<number> {
    const response = await createApiClient().deleteProcessed();
    return response.data.deleted;
}

/** Places the attachment `offsetSeconds` into the recording, at the call as a whole, or back at its detected time. */
export async function setAttachmentTime(attachmentId: string, mode: "offset" | "reference" | "detected", offsetSeconds = 0): Promise<void> {
    await createApiClient().setAttachmentTime({ attachmentId, mode, offsetSeconds });
}

/** Sets the attachment's note; blank removes it. */
export async function setAttachmentNote(attachmentId: string, note: string): Promise<void> {
    await createApiClient().setAttachmentNote({ attachmentId, note });
}

export async function deleteAttachment(attachmentId: string): Promise<void> {
    await createApiClient().deleteAttachment({ attachmentId });
}

/** Adds a note `offsetSeconds` into the recording, or without a time (null - the call as a whole). */
export async function addMark(recordingId: string, offsetSeconds: number | null, note: string): Promise<void> {
    await createApiClient().addMark({ recordingId, atTime: offsetSeconds !== null, offsetSeconds: offsetSeconds ?? 0, note });
}

/** Sets a mark's note; blank removes it. Plaud's late text never overwrites it afterwards. */
export async function setMarkNote(markId: string, note: string): Promise<void> {
    await createApiClient().setMarkNote({ markId, note });
}

/** Deletes a mark; the next sync does not import it again. */
export async function deleteMark(markId: string): Promise<void> {
    await createApiClient().deleteMark({ markId });
}
