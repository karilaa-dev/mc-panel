/// <reference types="vitest/config" />
import path from "path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig } from "vite"

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "./src"),
    },
  },
  build: {
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            {
              name: "react-vendor",
              test: /node_modules\/(?:react|react-dom|scheduler|react-router|react-router-dom)\//,
            },
            {
              name: "ui-vendor",
              test: /node_modules\/(?:@base-ui|lucide-react|sonner|cmdk)\//,
            },
            {
              name: "data-vendor",
              test: /node_modules\/(?:@tanstack|react-hook-form|zod|@hookform)\//,
            },
          ],
        },
      },
    },
  },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test-setup.ts",
    css: true,
    clearMocks: true,
  },
})
