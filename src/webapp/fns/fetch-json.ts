export class HttpError extends Error {
  constructor(public readonly status: number) {
    super(`Request failed with status ${status}`);
    this.name = "HttpError";
  }
}

export function httpStatus(error: unknown) {
  return error instanceof HttpError ? error.status : undefined;
}

export async function fetchJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { credentials: "include", ...init });
  if (!response.ok) {
    throw new HttpError(response.status);
  }
  return response.json();
}

export function retryServerErrors(failureCount: number, error: Error) {
  const status = httpStatus(error);
  return (status === undefined || status >= 500) && failureCount < 2;
}

export async function fetchNoContent(url: string, init?: RequestInit): Promise<void> {
  const response = await fetch(url, { credentials: "include", ...init });
  if (!response.ok) {
    throw new HttpError(response.status);
  }
}

export function jsonBody(method: string, body: unknown): RequestInit {
  return {
    method,
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify(body),
  };
}
