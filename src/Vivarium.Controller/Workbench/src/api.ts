export interface Session {
    localDevelopment?: boolean;
    user: { id: string; displayName: string; isInstallation: boolean } | null;
    canReadProfiles: boolean;
    canGrantRoles: boolean;
    canChangeUsers: boolean;
    requestVerificationToken: string;
}
const source = document.querySelector<HTMLScriptElement>("#vivarium-session");
if (!source) throw new Error("Session bootstrap is absent.");
export const session = JSON.parse(source.textContent ?? "") as Session;
export interface Page<T> { items: T[]; total: number; offset: number; limit: number }
export interface User { id: string; userName: string; displayName: string | null; isEnabled: boolean; isInstallation: boolean }
export interface Role { id: string; name: string; permissions: string[]; includedRoles: string[] }
export interface Grant { roleId: string; scope: string }

export class ApiError extends Error {
    constructor(public readonly status: number) {
        super(status === 401 ? "Sign in required." : status === 403 ? "Current permissions do not allow this action."
            : status === 404 ? "This item is no longer available." : status === 400 ? "Refresh and check the values."
            : status === 409 ? "The state changed. Refresh and try again." : status === 410 ? "This output is no longer available. Read the current output page."
            : status === 422 ? "This execution profile is not available."
            : "Service unavailable.");
    }
}
export async function api<T>(path: string, signal: AbortSignal, method = "GET", body?: unknown): Promise<T> {
    const headers: Record<string, string> = { Accept: "application/json" };
    if (method !== "GET") headers.RequestVerificationToken = session.requestVerificationToken;
    if (body !== undefined) headers["Content-Type"] = "application/json";
    const response = await fetch("/api/v1/" + path, { method, headers, signal, credentials: "same-origin",
        cache: "no-store", body: body === undefined ? undefined : JSON.stringify(body) });
    if (!response.ok) throw new ApiError(response.status);
    return response.status === 204 ? undefined as T : await response.json() as T;
}
