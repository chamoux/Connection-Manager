import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig, type Plugin } from 'vite'

// The settings window (WebView2, Task 16) serves dist/ from a virtual host. This policy allows only our own files:
// no inline script or style, no eval, no network beyond the origin.
const csp = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self'",
  "img-src 'self'",
  "font-src 'self'",
  "connect-src 'self'",
  "base-uri 'none'",
  "form-action 'none'",
  "object-src 'none'",
].join('; ')

const charset = '<meta charset="UTF-8" />'

// Builds only: the dev server injects inline styles for hot reload, which this policy would block.
function cspMeta(): Plugin {
  return {
    name: 'csp-meta',
    apply: 'build',
    transformIndexHtml(html) {
      if (!html.includes(charset)) throw new Error('index.html: charset meta not found, cannot place the CSP')
      return html.replace(charset, `${charset}\n    <meta http-equiv="Content-Security-Policy" content="${csp}" />`)
    },
  }
}

export default defineConfig({
  base: './',
  plugins: [vue(), tailwindcss(), cspMeta()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    assetsInlineLimit: 0, // no data: URIs, which img-src 'self' would block
    sourcemap: false,
  },
})
