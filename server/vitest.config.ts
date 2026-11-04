import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.jsonc" },
      miniflare: {
        // Test-only secrets and tighter limits so the limit tests run quickly.
        bindings: { READ_KEY: "test-read", WRITE_KEY: "test-write" },
      },
    }),
  ],
});
