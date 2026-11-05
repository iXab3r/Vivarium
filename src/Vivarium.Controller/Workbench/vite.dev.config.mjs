import { defineConfig } from 'vite';
import { readFileSync } from 'node:fs';
import { Agent } from 'node:https';
import { fileURLToPath } from 'node:url';

// The launcher supplies private, owned TLS material. No system-wide trust change.
const root = process.env.VIVARIUM_LOCAL_DEV_ROOT;
if (!root) throw new Error('Use tools/local-dev/local-dev.ps1 start.');
const state = JSON.parse(readFileSync(`${root}/owner.json`, 'utf8'));
const backend = new URL(state.backend);
const origin = new URL(state.origin);
if ([backend, origin].some(u => u.hostname !== '127.0.0.1' || u.protocol !== 'https:'))
    throw new Error('Local development requires loopback HTTPS.');
const cert = readFileSync(`${root}/https.pem`);
export default defineConfig({
    base: '/workbench/',
    server: {
        fs: { strict: true, allow: [fileURLToPath(new URL('.', import.meta.url))],
            deny: ['**/.git/**', '**/*.pem', '**/*.pfx', '**/*.private.*'] },
        host: '127.0.0.1', port: Number(origin.port), strictPort: true,
        https: { cert, key: readFileSync(`${root}/https-key.pem`) },
        cors: false,
        hmr: { protocol: 'wss', host: '127.0.0.1', clientPort: Number(origin.port) },
        proxy: {
            '^/(?!workbench(?:/|$))': {
                target: backend.origin, changeOrigin: false,
                agent: new Agent({ ca: cert, rejectUnauthorized: true }),
            },
        },
    },
});
