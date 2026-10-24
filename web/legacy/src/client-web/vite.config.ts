import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') }
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5858', changeOrigin: true },
    }
  },
  build: {
    outDir: '../Pim.Api/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 600,
    rollupOptions: {
      output: {
        manualChunks: {
          echarts: ['echarts'],
          react: ['react', 'react-dom', 'react-router-dom'],
          vendor: ['@tanstack/react-query', 'date-fns', 'luxon', 'leaflet', 'react-leaflet'],
        },
      },
    },
  },
  define: {
    __APP_VERSION__: JSON.stringify(process.env.VITE_APP_VERSION || '0.0.0-local'),
    __GIT_SHA__: JSON.stringify(process.env.VITE_GIT_SHA || process.env.GITHUB_SHA?.slice(0,7) || 'local')
  }
})
