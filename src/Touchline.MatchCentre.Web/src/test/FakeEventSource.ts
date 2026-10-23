import { vi } from 'vitest';

/** A stand-in for the browser's `EventSource`, which jsdom doesn't have. Tests push named messages with `emit`. */
export class FakeEventSource {
  /** Every source opened since the last `install`, oldest first. */
  static instances: FakeEventSource[] = [];

  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;

  readonly url: string;
  closed = false;
  /** Mirrors `EventSource.readyState`: OPEN until `close` or `fail`. */
  readyState: number = FakeEventSource.OPEN;
  private readonly listeners = new Map<string, ((e: MessageEvent<string>) => void)[]>();

  constructor(url: string | URL) {
    this.url = String(url);
    FakeEventSource.instances.push(this);
  }

  /** Replaces the global `EventSource` with this fake for the current test. */
  static install(): void {
    FakeEventSource.instances = [];
    vi.stubGlobal('EventSource', FakeEventSource);
  }

  /** The most recently opened source. */
  static latest(): FakeEventSource {
    const source = FakeEventSource.instances.at(-1);
    if (!source) throw new Error('No EventSource was opened.');
    return source;
  }

  addEventListener(type: string, listener: (e: MessageEvent<string>) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  removeEventListener(type: string, listener: (e: MessageEvent<string>) => void): void {
    this.listeners.set(
      type,
      (this.listeners.get(type) ?? []).filter((l) => l !== listener),
    );
  }

  close(): void {
    this.closed = true;
    this.readyState = FakeEventSource.CLOSED;
  }

  /** Delivers one named message with a JSON data line and, when given, an `id` line, as the server would. */
  emit(type: string, data: unknown, id?: number | string): void {
    if (this.closed) return;
    const event = new MessageEvent<string>(type, {
      data: JSON.stringify(data),
      lastEventId: id === undefined ? '' : String(id),
    });
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }

  /** The browser giving up for good (say, a 503 during a deploy): `readyState` becomes CLOSED and `error` fires. */
  fail(): void {
    this.readyState = FakeEventSource.CLOSED;
    const event = new Event('error') as MessageEvent<string>;
    for (const listener of this.listeners.get('error') ?? []) listener(event);
  }
}
