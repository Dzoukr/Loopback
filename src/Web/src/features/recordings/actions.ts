"use server";

import { createApiClient } from "@/api";
import type { WorkflowDto, RecordingDto, SyncStatusDto } from "@/lib/generated/api-client";

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
