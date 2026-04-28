import react from '@vitejs/plugin-react';
import { defineConfig, loadEnv } from 'vite';

/** Optional dev proxy so you can omit VITE_API_BASE and use same-origin /api → backend. */
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const target = env.VITE_DEV_PROXY_TARGET || 'http://localhost:5088';

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': { target, changeOrigin: true },
        '/ws': { target, changeOrigin: true, ws: true },
      },
    },
  };
});
