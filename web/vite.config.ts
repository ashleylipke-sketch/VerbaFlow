import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/VerbaFlow.Api/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://localhost:5080' } },
});
