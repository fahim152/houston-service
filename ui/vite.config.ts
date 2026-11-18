import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The browser only talks to this dev server; /api/* is forwarded to the Rockets service.
// That avoids CORS, so the service needs no changes. Override the target with ROCKETS_API=http://host:port.
const target = process.env.ROCKETS_API ?? "http://localhost:8088";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target, changeOrigin: true, rewrite: (path) => path.replace(/^\/api/, "") },
    },
  },
});
