import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@property-search/shared-contracts': resolve(
        __dirname, '../../libs/shared-contracts/src/index.ts'
      ),
      '@property-search/shared-validation': resolve(
        __dirname, '../../libs/shared-validation/src/index.ts'
      ),
      '@property-search/shared-types': resolve(
        __dirname, '../../libs/shared-types/src/index.ts'
      )
    }
  },
  server: { port: 4200 }
});