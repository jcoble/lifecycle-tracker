import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';

const apiTarget = process.env.API_URL || 'http://localhost:5556';

export default defineConfig({
	plugins: [tailwindcss(), sveltekit()],
	server: {
		port: 5555,
		strictPort: true,
		host: true,
		proxy: {
			'/api': {
				target: apiTarget,
				changeOrigin: true
			}
		}
	}
});
