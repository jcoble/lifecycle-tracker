import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';

export default defineConfig({
	plugins: [tailwindcss(), sveltekit()],
	server: {
		port: 5557,
		proxy: {
			'/api': {
				target: 'http://localhost:5556',
				changeOrigin: true
			}
		}
	}
});
