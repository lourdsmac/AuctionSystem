/**
 * Resolves HTTP API base. Empty string = same origin (use Vite dev proxy in development).
 */
export function getHttpBase(): string {
  const raw = import.meta.env.VITE_API_BASE?.trim();
  return raw ? raw.replace(/\/$/, '') : '';
}

export function httpUrl(path: string): string {
  const base = getHttpBase();
  return `${base}${path.startsWith('/') ? path : `/${path}`}`;
}

/** Build WebSocket URL for a path like `/ws/auction`. */
export function webSocketUrl(path: string): string {
  const base = getHttpBase();
  if (!base) {
    const { protocol, host } = window.location;
    const wsProto = protocol === 'https:' ? 'wss:' : 'ws:';
    const p = path.startsWith('/') ? path : `/${path}`;
    return `${wsProto}//${host}${p}`;
  }

  const u = new URL(base, window.location.href);
  u.protocol = u.protocol === 'https:' ? 'wss:' : 'ws:';
  const p = path.startsWith('/') ? path : `/${path}`;
  return `${u.origin}${p}`;
}
