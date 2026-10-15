import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const api = 'http://localhost:5086'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: api, changeOrigin: true },
      // ws:true matters: without it the SignalR handshake falls back to long
      // polling in dev and the websocket upgrade silently fails.
      '/hub': { target: api, changeOrigin: true, ws: true },
    },
  },
  build: {
    // Build straight into the API's wwwroot so one `dotnet run` serves
    // everything in production.
    outDir: '../src/QuizLive.Api/wwwroot',
    emptyOutDir: true,
  },
})
