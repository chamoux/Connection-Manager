import { defineConfig } from 'vitest/config'

// Separate from vite.config.ts: vitest 3 ships its own vite, and the tests are plain TypeScript.
export default defineConfig({
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
})
