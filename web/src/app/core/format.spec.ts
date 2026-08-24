import {
  formatCost,
  formatDuration,
  formatInterval,
  formatPercent,
  formatTokens,
  modelShortName,
  relativeTime,
} from './format';

describe('formatCost', () => {
  it('formats zero, sub-cent, normal and large amounts', () => {
    expect(formatCost(0)).toBe('$0.00');
    expect(formatCost(0.0042)).toBe('$0.0042');
    expect(formatCost(1.5)).toBe('$1.50');
    expect(formatCost(1234)).toBe('$1,234');
  });
});

describe('formatTokens', () => {
  it('scales through k and M', () => {
    expect(formatTokens(950)).toBe('950');
    expect(formatTokens(12_400)).toBe('12.4k');
    expect(formatTokens(2_500_000)).toBe('2.50M');
  });
});

describe('formatDuration', () => {
  it('formats ms, seconds, minutes and hours', () => {
    expect(formatDuration(400)).toBe('400ms');
    expect(formatDuration(9_400)).toBe('9.4s');
    expect(formatDuration(59_000)).toBe('59s');
    expect(formatDuration(60_000)).toBe('1m');
    expect(formatDuration(150_000)).toBe('2m 30s');
    expect(formatDuration(3_900_000)).toBe('1h 5m');
  });

  it('never renders 60 seconds (rounding carry regression)', () => {
    // 239.6s used to render as "3m 60s".
    expect(formatDuration(239_600)).toBe('4m');
    expect(formatDuration(59_900)).toBe('60s'); // < 60s branch keeps plain seconds
    expect(formatDuration(119_800)).toBe('2m');
  });
});

describe('formatInterval', () => {
  it('reads naturally at minute and hour scales', () => {
    expect(formatInterval(15)).toBe('every 15 min');
    expect(formatInterval(60)).toBe('every hour');
    expect(formatInterval(240)).toBe('every 4 h');
    expect(formatInterval(90)).toBe('every 1.5 h');
  });
});

describe('formatPercent', () => {
  it('rounds a fraction to whole percent', () => {
    expect(formatPercent(0.928)).toBe('93%');
    expect(formatPercent(0)).toBe('0%');
    expect(formatPercent(1)).toBe('100%');
  });
});

describe('modelShortName', () => {
  it('shortens model ids for display', () => {
    expect(modelShortName('claude-opus-5')).toBe('Opus 5');
    expect(modelShortName('claude-haiku-4-5')).toBe('Haiku 4.5');
    expect(modelShortName('claude-sonnet-5')).toBe('Sonnet 5');
  });
});

describe('relativeTime', () => {
  it('handles null and recent timestamps', () => {
    expect(relativeTime(null)).toBe('—');
    expect(relativeTime(new Date().toISOString())).toBe('just now');
    const fiveMinAgo = new Date(Date.now() - 5 * 60_000).toISOString();
    expect(relativeTime(fiveMinAgo)).toBe('5 min ago');
    const inTwoHours = new Date(Date.now() + 2 * 3_600_000 + 30_000).toISOString();
    expect(relativeTime(inTwoHours)).toBe('in 2 h');
  });

  it('treats suffix-less timestamps as UTC (API sends UTC)', () => {
    const utcNow = new Date().toISOString().replace('Z', '');
    expect(relativeTime(utcNow)).toBe('just now');
  });
});
