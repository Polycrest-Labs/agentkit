import angular from '@analogjs/vite-plugin-angular';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  // The Angular compiler must process component sources: signal inputs / viewChild /
  // contentChildren are invisible to a plain esbuild transform (the JIT definition ends up with
  // NO registered inputs), and Node cannot parse raw decorator syntax either.
  plugins: [angular()],
  test: {
    environment: 'jsdom',
    include: ['src/**/*.spec.ts'],
    setupFiles: ['src/test-setup.ts'],
  },
});
