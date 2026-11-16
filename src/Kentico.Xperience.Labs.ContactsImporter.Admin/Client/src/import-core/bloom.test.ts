import { describe, expect, it } from 'vitest';

import { BloomFilter } from './bloom';

describe('BloomFilter', () => {
  it('is sized at about 12 MB for 10M keys at 1 % by default', () => {
    const filter = new BloomFilter();
    expect(filter.hashes).toBe(7);
    expect(filter.byteLength).toBeGreaterThan(11.5 * 1000 * 1000);
    expect(filter.byteLength).toBeLessThan(12.5 * 1000 * 1000);
  });

  it('never misses a key that was added', () => {
    // rate low enough that no false positive is expected while filling
    const filter = new BloomFilter({ expectedItems: 5000, falsePositiveRate: 1e-7 });
    for (let i = 0; i < 5000; i++) {
      expect(filter.add(`user${i}@example.com`)).toBe(false);
    }
    for (let i = 0; i < 5000; i++) {
      expect(filter.has(`user${i}@example.com`)).toBe(true);
      expect(filter.add(`user${i}@example.com`)).toBe(true);
    }
  });

  it.each([
    [0.01, 50_000],
    [0.001, 20_000],
  ])('keeps false positives near the configured rate (%f, %i keys)', (rate, items) => {
    const filter = new BloomFilter({ expectedItems: items, falsePositiveRate: rate });
    let flaggedOnAdd = 0;
    for (let i = 0; i < items; i++) {
      if (filter.add(`contact-${i}@example.com`)) {
        flaggedOnAdd++;
      }
    }

    const probes = 100_000;
    let falsePositives = 0;
    for (let i = 0; i < probes; i++) {
      if (filter.has(`other-${i}@example.org`)) {
        falsePositives++;
      }
    }

    // at full load the rate is ~= the configured rate; while filling it is lower
    expect(falsePositives / probes).toBeLessThan(rate * 1.5);
    expect(falsePositives / probes).toBeGreaterThan(rate * 0.5);
    expect(flaggedOnAdd / items).toBeLessThan(rate);
  });

  it('separates similar and non-ASCII keys', () => {
    const filter = new BloomFilter({ expectedItems: 1000, falsePositiveRate: 0.0001 });
    expect(filter.add('a')).toBe(false);
    expect(filter.add('b')).toBe(false);
    expect(filter.add('ab')).toBe(false);
    expect(filter.add('ba')).toBe(false);
    expect(filter.add('josé@example.com')).toBe(false);
    expect(filter.add('jose@example.com')).toBe(false);
    expect(filter.add('josé@example.com')).toBe(true);
  });

  it('validates options', () => {
    expect(() => new BloomFilter({ falsePositiveRate: 0 })).toThrowError(RangeError);
    expect(() => new BloomFilter({ falsePositiveRate: 1 })).toThrowError(RangeError);
    expect(() => new BloomFilter({ expectedItems: 1e12 })).toThrowError(RangeError);
  });
});
