import { defineConfig } from 'vitest/config'

export default defineConfig({
  server: { port: 5173, open: false },
  build: { target: 'es2022' },
  // A year of the full 39-body system is a few hundred thousand substeps; the physics tests need
  // more than vitest's 5 s default.
  test: { include: ['tests/**/*.test.ts'], testTimeout: 60000 },
})
