// Shared display formatting used across pages.

export function formatCost(usd: number): string {
  if (usd === 0) return '$0.00';
  if (usd < 0.01) return `$${usd.toFixed(4)}`;
  if (usd < 100) return `$${usd.toFixed(2)}`;
  return `$${Math.round(usd).toLocaleString('en-US')}`;
}

export function formatTokens(tokens: number): string {
  if (tokens < 1_000) return `${tokens}`;
  if (tokens < 1_000_000) return `${(tokens / 1_000).toFixed(1)}k`;
  return `${(tokens / 1_000_000).toFixed(2)}M`;
}

export function formatDuration(ms: number): string {
  if (ms < 1_000) return `${ms}ms`;
  const seconds = ms / 1_000;
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 1 : 0)}s`;
  const totalSeconds = Math.round(seconds);
  const minutes = Math.floor(totalSeconds / 60);
  const rest = totalSeconds % 60;
  if (minutes < 60) return rest > 0 ? `${minutes}m ${rest}s` : `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}

export function formatInterval(minutes: number): string {
  if (minutes < 60) return `every ${minutes} min`;
  if (minutes % 60 === 0) {
    const hours = minutes / 60;
    return hours === 1 ? 'every hour' : `every ${hours} h`;
  }
  return `every ${(minutes / 60).toFixed(1)} h`;
}

export function relativeTime(iso: string | null): string {
  if (!iso) return '—';
  const then = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso + 'Z').getTime();
  const diff = Date.now() - then;
  const future = diff < 0;
  const abs = Math.abs(diff);
  const minutes = Math.floor(abs / 60_000);
  let text: string;
  if (minutes < 1) text = future ? 'moments' : 'just now';
  else if (minutes < 60) text = `${minutes} min`;
  else if (minutes < 60 * 24) text = `${Math.floor(minutes / 60)} h`;
  else text = `${Math.floor(minutes / (60 * 24))} d`;
  if (minutes < 1 && !future) return text;
  return future ? `in ${text}` : `${text} ago`;
}

export function formatDateTime(iso: string | null): string {
  if (!iso) return '—';
  const date = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso + 'Z');
  return date.toLocaleString('en-GB', {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  });
}

export function formatPercent(fraction: number): string {
  return `${Math.round(fraction * 100)}%`;
}

/** A metric reading with its unit: 1,234 sign-ups · 3.4% · $12.50 · 250 ms. */
export function formatMetric(value: number, unit = ''): string {
  const u = unit.trim();
  const abs = Math.abs(value);
  const num = Number.isInteger(value)
    ? value.toLocaleString('en-US')
    : abs >= 100
      ? Math.round(value).toLocaleString('en-US')
      : abs >= 10
        ? value.toFixed(1)
        : value.toFixed(2).replace(/\.?0+$/, '');
  if (!u) return num;
  if (u === '%') return `${num}%`;
  if (u === '$' || u === '€' || u === '£') return `${u}${num}`;
  return `${num} ${u}`;
}

export function modelShortName(model: string): string {
  return model
    .replace('claude-', '')
    .replace(/-(\d)-(\d)/, ' $1.$2')
    .replace(/-(\d)$/, ' $1')
    .replace(/^([a-z])/, c => c.toUpperCase());
}
