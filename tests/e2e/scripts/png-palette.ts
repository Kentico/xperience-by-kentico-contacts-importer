import { deflateSync, inflateSync } from "node:zlib";

// Shrinks a Playwright PNG screenshot (8-bit RGB or RGBA, not interlaced) to an indexed PNG with at most
// 256 colors. The administration's background gradient makes true-color screenshots large; with a
// palette they are a fraction of the size. Used only by capture-readme-screenshots.ts.

const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

interface Chunk {
  type: string;
  data: Buffer;
}

function readChunks(png: Buffer): Chunk[] {
  if (!png.subarray(0, 8).equals(signature)) {
    throw new Error("Not a PNG file.");
  }
  const chunks: Chunk[] = [];
  let offset = 8;
  while (offset < png.length) {
    const length = png.readUInt32BE(offset);
    const type = png.toString("latin1", offset + 4, offset + 8);
    chunks.push({ type, data: png.subarray(offset + 8, offset + 8 + length) });
    offset += 12 + length;
  }
  return chunks;
}

function paeth(a: number, b: number, c: number): number {
  const p = a + b - c;
  const pa = Math.abs(p - a);
  const pb = Math.abs(p - b);
  const pc = Math.abs(p - c);
  return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
}

// Returns RGB pixels (3 bytes each)
function decode(png: Buffer): { width: number; height: number; rgb: Uint8Array } {
  const chunks = readChunks(png);
  const header = chunks.find((chunk) => chunk.type === "IHDR")!.data;
  const width = header.readUInt32BE(0);
  const height = header.readUInt32BE(4);
  const bitDepth = header[8];
  const colorType = header[9];
  const interlace = header[12];
  if (bitDepth !== 8 || (colorType !== 2 && colorType !== 6) || interlace !== 0) {
    throw new Error(`Unsupported PNG (bit depth ${bitDepth}, color type ${colorType}, interlace ${interlace}).`);
  }

  const channels = colorType === 6 ? 4 : 3;
  const stride = width * channels;
  const raw = inflateSync(Buffer.concat(chunks.filter((chunk) => chunk.type === "IDAT").map((chunk) => chunk.data)));
  const pixels = new Uint8Array(stride * height);

  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const source = y * (stride + 1) + 1;
    const row = y * stride;
    const previous = row - stride;
    for (let x = 0; x < stride; x++) {
      const value = raw[source + x];
      const left = x >= channels ? pixels[row + x - channels] : 0;
      const up = y > 0 ? pixels[previous + x] : 0;
      const upLeft = y > 0 && x >= channels ? pixels[previous + x - channels] : 0;
      let result: number;
      switch (filter) {
        case 0: result = value; break;
        case 1: result = value + left; break;
        case 2: result = value + up; break;
        case 3: result = value + ((left + up) >> 1); break;
        case 4: result = value + paeth(left, up, upLeft); break;
        default: throw new Error(`Unknown PNG filter ${filter}.`);
      }
      pixels[row + x] = result & 0xff;
    }
  }

  if (channels === 3) {
    return { width, height, rgb: pixels };
  }
  // Screenshots are opaque: the alpha channel is dropped
  const rgb = new Uint8Array(width * height * 3);
  for (let i = 0, j = 0; i < pixels.length; i += 4, j += 3) {
    rgb[j] = pixels[i];
    rgb[j + 1] = pixels[i + 1];
    rgb[j + 2] = pixels[i + 2];
  }
  return { width, height, rgb };
}

function chunk(type: string, data: Buffer): Buffer {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const typeAndData = Buffer.concat([Buffer.from(type, "latin1"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(typeAndData));
  return Buffer.concat([length, typeAndData, crc]);
}

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) {
    c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  }
  return c >>> 0;
});

function crc32(data: Buffer): number {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc = crcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

// Palette: the 256 most frequent colors at 5 bits per channel (averaged); every pixel takes the nearest entry.
export function toPalettePng(png: Buffer): Buffer {
  const { width, height, rgb } = decode(png);
  const pixelCount = width * height;
  const bucketOf = (i: number) => ((rgb[i] >> 3) << 10) | ((rgb[i + 1] >> 3) << 5) | (rgb[i + 2] >> 3);

  const counts = new Uint32Array(32768);
  const sums = new Float64Array(32768 * 3);
  for (let p = 0, i = 0; p < pixelCount; p++, i += 3) {
    const bucket = bucketOf(i);
    counts[bucket]++;
    sums[bucket * 3] += rgb[i];
    sums[bucket * 3 + 1] += rgb[i + 1];
    sums[bucket * 3 + 2] += rgb[i + 2];
  }

  const buckets = Array.from(counts.keys()).filter((bucket) => counts[bucket] > 0);
  buckets.sort((a, b) => counts[b] - counts[a]);
  const palette = buckets.slice(0, 256).map((bucket) => [0, 1, 2].map((c) => Math.round(sums[bucket * 3 + c] / counts[bucket])));

  const indexOfBucket = new Int16Array(32768).fill(-1);
  const nearest = (bucket: number): number => {
    if (indexOfBucket[bucket] >= 0) {
      return indexOfBucket[bucket];
    }
    const [r, g, b] = [0, 1, 2].map((c) => sums[bucket * 3 + c] / counts[bucket]);
    let best = 0;
    let bestDistance = Infinity;
    palette.forEach(([pr, pg, pb], index) => {
      const distance = (pr - r) ** 2 * 0.3 + (pg - g) ** 2 * 0.59 + (pb - b) ** 2 * 0.11;
      if (distance < bestDistance) {
        bestDistance = distance;
        best = index;
      }
    });
    indexOfBucket[bucket] = best;
    return best;
  };

  const raw = Buffer.alloc((width + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width + 1)] = 0;
    for (let x = 0; x < width; x++) {
      raw[y * (width + 1) + 1 + x] = nearest(bucketOf((y * width + x) * 3));
    }
  }

  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bit depth
  header[9] = 3; // indexed color
  return Buffer.concat([
    signature,
    chunk("IHDR", header),
    chunk("PLTE", Buffer.from(palette.flat())),
    chunk("IDAT", deflateSync(raw, { level: 9 })),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}
