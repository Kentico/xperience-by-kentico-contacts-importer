export interface BloomFilterOptions {
  /** Number of distinct keys the filter is sized for. Default 10,000,000. */
  expectedItems?: number;
  /** False positive rate at `expectedItems` keys. Default 0.01. */
  falsePositiveRate?: number;
}

export const DEFAULT_BLOOM_EXPECTED_ITEMS = 10_000_000;
export const DEFAULT_BLOOM_FALSE_POSITIVE_RATE = 0.01;

function murmur3(key: string, seed: number): number {
  let h = seed;
  for (let i = 0; i < key.length; i++) {
    let k = key.charCodeAt(i);
    k = Math.imul(k, 0xcc9e2d51);
    k = (k << 15) | (k >>> 17);
    k = Math.imul(k, 0x1b873593);
    h ^= k;
    h = (h << 13) | (h >>> 19);
    h = (Math.imul(h, 5) + 0xe6546b64) | 0;
  }

  h ^= key.length;
  h ^= h >>> 16;
  h = Math.imul(h, 0x85ebca6b);
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35);
  h ^= h >>> 16;

  return h >>> 0;
}

/**
 * Bloom filter over string keys, used to warn about likely duplicate dedupe keys (D18).
 * Default sizing (10M keys at 1 %) takes about 12 MB. No false negatives; false positives
 * at roughly the configured rate once `expectedItems` keys were added.
 */
export class BloomFilter {
  /** Number of bits. */
  readonly size: number;
  /** Number of hash functions. */
  readonly hashes: number;
  private readonly bits: Uint8Array;

  constructor(options: BloomFilterOptions = {}) {
    const items = Math.max(
      1,
      options.expectedItems ?? DEFAULT_BLOOM_EXPECTED_ITEMS,
    );
    const rate = options.falsePositiveRate ?? DEFAULT_BLOOM_FALSE_POSITIVE_RATE;
    if (!(rate > 0 && rate < 1)) {
      throw new RangeError("falsePositiveRate must be between 0 and 1.");
    }

    const bits = Math.ceil((-items * Math.log(rate)) / (Math.LN2 * Math.LN2));
    if (bits >= 2 ** 32) {
      throw new RangeError(
        "Bloom filter too large; lower expectedItems or raise falsePositiveRate.",
      );
    }

    this.size = Math.max(64, bits);
    this.hashes = Math.max(1, Math.round((this.size / items) * Math.LN2));
    this.bits = new Uint8Array(Math.ceil(this.size / 8));
  }

  /** Memory used by the bit array. */
  get byteLength(): number {
    return this.bits.byteLength;
  }

  /** Adds the key. Returns true when the key was probably added before. */
  add(key: string): boolean {
    const h1 = murmur3(key, 0x9747b28c);
    const h2 = murmur3(key, 0x5bd1e995) | 1;
    let present = true;
    for (let i = 0; i < this.hashes; i++) {
      // h1 + i * h2 < 2^36: exact in a double
      const bit = (h1 + i * (h2 >>> 0)) % this.size;
      const byte = bit >>> 3;
      const mask = 1 << (bit & 7);
      if ((this.bits[byte] & mask) === 0) {
        present = false;
        this.bits[byte] |= mask;
      }
    }

    return present;
  }

  /** Whether the key was probably added. */
  has(key: string): boolean {
    const h1 = murmur3(key, 0x9747b28c);
    const h2 = murmur3(key, 0x5bd1e995) | 1;
    for (let i = 0; i < this.hashes; i++) {
      const bit = (h1 + i * (h2 >>> 0)) % this.size;
      if ((this.bits[bit >>> 3] & (1 << (bit & 7))) === 0) {
        return false;
      }
    }

    return true;
  }
}
