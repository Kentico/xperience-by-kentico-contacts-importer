import { defineConfig } from "vitest/config";

// Unit tests of the import core (src/import-core). Node environment: the code under test uses
// only web-standard APIs (streams, TextDecoder, CompressionStream, Web Crypto) that Node provides.
export default defineConfig({
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
  },
});
