/** Validation errors by field path (`name`, `squad[3].name`, `home`), as a 400's `errors` carries them. */
export type FieldErrors = Readonly<Record<string, readonly string[]>>;

/** The longest validation message kept; anything longer is cut, so a misbehaving server can't flood the page. */
const maxMessageLength = 300;

/**
 * The field errors in a 400 body (HttpValidationProblemDetails). Only `errors` is read: these are the validation
 * messages written for the fan. The body's `detail` and anything else are never kept. Anything malformed is dropped.
 */
export function fieldErrors(body: unknown): FieldErrors {
  if (typeof body !== 'object' || body === null) return {};
  const errors = (body as { errors?: unknown }).errors;
  if (typeof errors !== 'object' || errors === null || Array.isArray(errors)) return {};
  const found: Record<string, string[]> = {};
  for (const [path, messages] of Object.entries(errors)) {
    if (!Array.isArray(messages)) continue;
    const kept = messages
      .filter((m): m is string => typeof m === 'string' && m.trim().length > 0)
      .map((m) => m.slice(0, maxMessageLength));
    if (kept.length > 0) found[path] = kept;
  }
  return found;
}

/**
 * A problem's `type` URI (RFC 9457), used only to tell apart responses that share a status code. It's matched against
 * the stable identifiers in `ProblemTypes.cs`, never shown, and never read from the title, which is prose.
 */
export function problemType(body: unknown): string | undefined {
  if (typeof body !== 'object' || body === null) return undefined;
  const type = (body as { type?: unknown }).type;
  return typeof type === 'string' ? type : undefined;
}

/**
 * Seconds to wait from a `Retry-After` header: either delay-seconds or an HTTP date (RFC 9110). Null when it's
 * missing or unreadable.
 */
export function retryAfterSeconds(header: string | null | undefined, now: number = Date.now()): number | null {
  if (!header) return null;
  const value = header.trim();
  if (/^\d+$/.test(value)) return Number.parseInt(value, 10);
  const at = Date.parse(value);
  if (Number.isNaN(at)) return null;
  return Math.max(0, Math.ceil((at - now) / 1000));
}

/** A wait in words: "about 45 seconds", "about 2 minutes", "about 15 hours" (the day's fan matches reset at midnight UTC). */
export function describeWait(seconds: number): string {
  if (seconds < 60) return `about ${seconds} second${seconds === 1 ? '' : 's'}`;
  if (seconds >= 2 * 60 * 60) {
    const hours = Math.round(seconds / 3600);
    return `about ${hours} hours`;
  }
  const minutes = Math.ceil(seconds / 60);
  return `about ${minutes} minute${minutes === 1 ? '' : 's'}`;
}
