"use server";

import { createApiClient } from "@/api";
import type { WorkflowDto, RecordingDto, SyncStatusDto, TranscriptSegmentDto } from "@/lib/generated/api-client";

// Every backend call goes through these server actions (BFF) - components never call the backend.
// The audio is binary, so it goes through a route handler instead (app/api/recordings/[id]/audio).

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
