import { Api } from '@/lib/generated/api-client';
import { env } from '@/env';

/**
 * Backend API client for server actions. Loopback runs locally for a single user,
 * so there is no authentication. Regenerate the client with `yarn generate:api`
 * while the server runs in Development (it serves /openapi/v1.json).
 */
export function createApiClient() {
    return new Api({ baseURL: env.API_URL }).api;
}
