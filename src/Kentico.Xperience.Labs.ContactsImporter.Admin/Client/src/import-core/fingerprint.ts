import { concatBytes, sha256Hex } from "./bytes";
import type { FileLike } from "./source";

/** Bytes hashed from each end of the file. */
export const FINGERPRINT_EDGE_BYTES = 1024 * 1024;

/**
 * Cheap identity of a file for upload resume (`job.json` `file.fingerprint`): lowercase hex
 * SHA-256 of `"{size}:"` + first MiB + last MiB (the whole file when it is 2 MiB or smaller).
 * Reads at most 2 MiB whatever the file size.
 */
export async function fingerprintFile(
  file: FileLike,
  edgeBytes: number = FINGERPRINT_EDGE_BYTES,
): Promise<string> {
  const size = file.size;
  const parts: Uint8Array[] = [new TextEncoder().encode(`${size}:`)];
  if (size <= edgeBytes * 2) {
    parts.push(new Uint8Array(await file.slice(0, size).arrayBuffer()));
  } else {
    parts.push(new Uint8Array(await file.slice(0, edgeBytes).arrayBuffer()));
    parts.push(
      new Uint8Array(await file.slice(size - edgeBytes, size).arrayBuffer()),
    );
  }

  return sha256Hex(concatBytes(parts));
}
