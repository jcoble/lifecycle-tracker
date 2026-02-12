<script lang="ts">
	import KanbanBoard from '$lib/components/board/KanbanBoard.svelte';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import { milestones as milestonesApi } from '$lib/api/endpoints/milestones';
	import { labels as labelsApi } from '$lib/api/endpoints/labels';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';

	const queryClient = useQueryClient();

	const tasksQuery = createQuery(() => ({
		queryKey: ['tasks', getCurrentProjectId()],
		queryFn: () => tasksApi.list({ projectId: String(getCurrentProjectId()) }),
	}));

	// Load milestones for the current project
	const milestonesQuery = createQuery(() => ({
		queryKey: ['milestones', getCurrentProjectId()],
		queryFn: () => milestonesApi.list(getCurrentProjectId()),
	}));

	// Derive active milestones (InProgress first, then all)
	let activeMilestones = $derived(
		milestonesQuery.data?.filter(m => m.status === 'InProgress') ?? []
	);
	let allMilestones = $derived(milestonesQuery.data ?? []);

	// Load phases for all active milestones (or all milestones if none active)
	const phasesQuery = createQuery(() => ({
		queryKey: ['phases', 'board', allMilestones.map(m => m.id)],
		queryFn: async () => {
			const targets = allMilestones;
			if (!targets.length) return [];
			const results = await Promise.all(
				targets.map(m => phasesApi.list(m.id))
			);
			return results.flat();
		},
		enabled: milestonesQuery.isSuccess && allMilestones.length > 0,
	}));

	const labelsQuery = createQuery(() => ({
		queryKey: ['labels', getCurrentProjectId()],
		queryFn: () => labelsApi.list(getCurrentProjectId()),
	}));

	function handleTaskUpdated() {
		queryClient.invalidateQueries({ queryKey: ['tasks'] });
	}

	// Auto-redirect to list view on mobile (unless user explicitly prefers kanban)
	onMount(() => {
		const isMobile = window.matchMedia('(max-width: 768px)').matches;
		const pref = localStorage.getItem('lifecycle-board-view');
		if (isMobile && pref !== 'kanban') {
			goto('/board/list', { replaceState: true });
		}
	});
</script>

<svelte:head>
	<title>Board - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full">
	{#if tasksQuery.isLoading}
		<div class="flex h-full items-center justify-center">
			<p class="text-text-tertiary">Loading board...</p>
		</div>
	{:else if tasksQuery.isError}
		<div class="flex h-full items-center justify-center">
			<p class="text-danger">Failed to load tasks</p>
		</div>
	{:else}
		<KanbanBoard
			tasks={tasksQuery.data || []}
			phases={phasesQuery.data || []}
			milestones={allMilestones}
			labels={labelsQuery.data || []}
			onTaskUpdated={handleTaskUpdated}
		/>
	{/if}
</div>
