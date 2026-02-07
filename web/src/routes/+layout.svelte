<script lang="ts">
	import '../app.css';
	import { page } from '$app/stores';
	import { goto } from '$app/navigation';
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import {
		LayoutDashboard,
		Columns3,
		GitBranch,
		Target,
		BarChart3,
		Settings,
		Users,
		PanelLeftClose,
		PanelLeftOpen,
		Zap
	} from '@lucide/svelte';
	import { matchShortcut } from '$lib/shortcuts';
	import ShortcutsHelp from '$lib/components/shared/ShortcutsHelp.svelte';

	let { children } = $props();

	const queryClient = new QueryClient({
		defaultOptions: {
			queries: {
				staleTime: 30000,
				refetchOnWindowFocus: false,
			},
		},
	});

	let sidebarCollapsed = $state(false);
	let isMobile = $state(false);
	let showShortcuts = $state(false);

	const navItems = [
		{ href: '/', label: 'Dashboard', icon: LayoutDashboard },
		{ href: '/board', label: 'Board', icon: Columns3 },
		{ href: '/phases', label: 'Phases', icon: GitBranch },
		{ href: '/milestones', label: 'Milestones', icon: Target },
		{ href: '/metrics', label: 'Metrics', icon: BarChart3 },
		{ href: '/team', label: 'Team', icon: Users },
		{ href: '/settings', label: 'Settings', icon: Settings },
	];

	function isActive(href: string, pathname: string): boolean {
		if (href === '/') return pathname === '/';
		return pathname.startsWith(href);
	}

	// Responsive sidebar: auto-collapse on narrow viewports
	$effect(() => {
		const mql = window.matchMedia('(max-width: 768px)');
		const handleChange = (e: MediaQueryListEvent | MediaQueryList) => {
			isMobile = e.matches;
			if (e.matches) sidebarCollapsed = true;
		};
		handleChange(mql);
		mql.addEventListener('change', handleChange);
		return () => mql.removeEventListener('change', handleChange);
	});

	function handleNavClick() {
		if (isMobile) sidebarCollapsed = true;
	}

	$effect(() => {
		const eventSource = new EventSource('/api/events?projectId=1');

		const invalidateAll = () => {
			queryClient.invalidateQueries({ queryKey: ['tasks'] });
			queryClient.invalidateQueries({ queryKey: ['dashboard'] });
		};

		eventSource.addEventListener('task:created', invalidateAll);
		eventSource.addEventListener('task:updated', invalidateAll);
		eventSource.addEventListener('task:deleted', invalidateAll);
		eventSource.addEventListener('phase:updated', () => {
			queryClient.invalidateQueries({ queryKey: ['phases'] });
			queryClient.invalidateQueries({ queryKey: ['dashboard'] });
		});
		eventSource.addEventListener('milestone:updated', () => {
			queryClient.invalidateQueries({ queryKey: ['milestones'] });
			queryClient.invalidateQueries({ queryKey: ['dashboard'] });
		});
		eventSource.addEventListener('comment:created', () => {
			queryClient.invalidateQueries({ queryKey: ['comments'] });
		});
		eventSource.addEventListener('activity', () => {
			queryClient.invalidateQueries({ queryKey: ['activity'] });
			queryClient.invalidateQueries({ queryKey: ['dashboard'] });
		});

		return () => eventSource.close();
	});

	function handleKeydown(e: KeyboardEvent) {
		const action = matchShortcut(e);
		if (!action) return;

		switch (action) {
			case 'show-shortcuts':
				showShortcuts = !showShortcuts;
				break;
			case 'close-panel':
				showShortcuts = false;
				break;
			case 'new-task':
				goto('/board');
				break;
			case 'focus-search':
				// Dispatch custom event for board search to pick up
				window.dispatchEvent(new CustomEvent('lifecycle:focus-search'));
				break;
			case 'command-palette':
				// Future: open command palette
				break;
		}
	}
</script>

<svelte:window onkeydown={handleKeydown} />

<QueryClientProvider client={queryClient}>
	<div class="flex h-screen">
		<!-- Sidebar -->
		<aside
			class="flex flex-col border-r border-border bg-surface transition-all duration-200 {sidebarCollapsed ? 'w-16' : 'w-56'} shrink-0"
		>
			<!-- Logo -->
			<div class="flex items-center gap-2 border-b border-border px-4 py-4">
				<Zap class="h-5 w-5 shrink-0 text-accent" />
				{#if !sidebarCollapsed}
					<span class="font-semibold text-text-primary truncate">Lifecycle</span>
				{/if}
			</div>

			<!-- Nav -->
			<nav class="flex-1 space-y-1 px-2 py-3">
				{#each navItems as item}
					{@const active = isActive(item.href, $page.url.pathname)}
					{@const Icon = item.icon}
					<a
						href={item.href}
						onclick={handleNavClick}
						class="flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors
							{active
							? 'bg-accent/10 text-accent'
							: 'text-text-secondary hover:bg-surface-hover hover:text-text-primary'}
							{sidebarCollapsed ? 'justify-center' : ''}"
						title={sidebarCollapsed ? item.label : undefined}
					>
						<Icon class="h-4 w-4 shrink-0" />
						{#if !sidebarCollapsed}
							<span>{item.label}</span>
						{/if}
					</a>
				{/each}
			</nav>

			<!-- Collapse toggle -->
			<div class="border-t border-border px-2 py-3">
				<button
					onclick={() => (sidebarCollapsed = !sidebarCollapsed)}
					class="flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm text-text-secondary transition-colors hover:bg-surface-hover hover:text-text-primary {sidebarCollapsed ? 'justify-center' : ''}"
				>
					{#if sidebarCollapsed}
						<PanelLeftOpen class="h-4 w-4 shrink-0" />
					{:else}
						<PanelLeftClose class="h-4 w-4 shrink-0" />
						<span>Collapse</span>
					{/if}
				</button>
			</div>
		</aside>

		<!-- Main content -->
		<main class="flex-1 overflow-auto min-w-0">
			{@render children()}
		</main>
	</div>

	<!-- Shortcuts help dialog -->
	<ShortcutsHelp bind:open={showShortcuts} />
</QueryClientProvider>
