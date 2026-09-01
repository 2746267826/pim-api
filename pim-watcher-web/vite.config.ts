import { defineConfig } from 'vite'
import { resolve } from 'path'

export default defineConfig({
  build: {
    minify: false,
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      input: {
        'background/main': resolve(__dirname, 'src/background/main.ts'),
        offscreen: resolve(__dirname, 'src/offscreen.ts'),
      },
      output: {
        entryFileNames: '[name].js',
        chunkFileNames: 'chunks/[name]-[hash].js',
        assetFileNames: 'assets/[name][extname]',
      },
    },
  },
})
