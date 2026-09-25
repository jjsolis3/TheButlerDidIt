import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vite'

// In development the React app runs on Vite's dev server (port 5173) and the
// API on ASP.NET Core (port 5080). The proxy forwards API, hub and media calls,
// so the browser sees one origin, just like in production where ASP.NET serves
// the built files itself.
const api = process.env.API_URL ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': api,
      '/media': api,
      '/healthz': api,
      '/hubs': { target: api, ws: true },
    },
  },
  build: {
    // Output straight into the API's wwwroot so `dotnet run` serves the latest build.
    outDir: '../ButlerDidIt.Api/wwwroot',
    emptyOutDir: true,
  },
})
