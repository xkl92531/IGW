import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

// 构建产物直接进 Runtime 的 wwwroot(自包含发布会一起打包,Win/Linux 同一套 UI)
// 开发时 /api、/iotgateway 代理到本机运行时 48020
export default defineConfig({
  plugins: [vue()],
  base: './',
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir: fileURLToPath(new URL('../IGW.Runtime/wwwroot', import.meta.url)),
    emptyOutDir: true,
    chunkSizeWarningLimit: 1000,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes('node_modules')) return undefined
          if (id.includes('element-plus') || id.includes('@element-plus')) return 'vendor-element-plus'
          if (id.includes('vue-router') || id.includes('pinia') || /node_modules[\\/]vue[\\/]/.test(id)) return 'vendor-vue'
          if (id.includes('axios')) return 'vendor-http'
          return 'vendor'
        },
      },
    },
  },
  server: {
    port: 5199,
    proxy: {
      '/hubs': { target: 'http://127.0.0.1:48020', ws: true },
      '/api': 'http://127.0.0.1:48020',
      '/iotgateway': 'http://127.0.0.1:48020',
    },
  },
})
