<script lang="ts">
	import { page } from '$app/stores';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import BoardCard from '$lib/components/board/BoardCard.svelte';
	import TaskDetail from '$lib/components/tasks/TaskDetail.svelte';
	import type { Task } from '$lib/types';
	import { marked } from 'marked';
	import { ArrowLeft } from '@lucide/svelte';

	const queryClient = useQueryClient();
	let id = $derived(Number($page.params.id));

	const phaseQuery = createQuery(() => ({
		queryKey: ['phases', id],
		queryFn: () => phasesApi.get(id),
	}));

	const tasksQuery = createQuery(() => ({
		queryKey: ['tasks', { phaseId: id }],
		queryFn: () => tasksApi.list({ phaseId: String(id) }),
	}));

	let selectedTask = $state<Task | null>(null);
	let renderedCriteria = $derived(
		phaseQuery.data?.successCriteria
			? (marked.parse(phaseQuery.data.successCriteria) as string)
			: ''
	);

	function handleTaskClick(task: Task) {
		selectedTask = task;
	}

	function handleTaskUpdate() {
		selectedTask = null;
		queryClient.invalidateQueries({ queryKey: ['tasks', { phaseId: id }] });
	}
</script>

<svelte:head>
	<title>{phaseQuery.data?.name || 'Phase'} - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	{#if phaseQuery.isLoading}
		<p class="text-text-tertiary">Loading phase...</p>
	{:else if phaseQuery.data}
		{@const phase = phaseQuery.data}

		<a href="/phases" class="mb-4 inline-flex items-center gap-1 text-sm text-text-tertiary hover:text-text-secondary">
			<ArrowLeft class="h-3.5 w-3.5" />
			All Phases
		</a>

		<div class="mb-6">
			<div class="flex items-center gap-3">
				<h1 class="text-2xl font-bold text-text-primary">{phase.name}</h1>
				<StatusBadge status={phase.status} />
			</div>
			{#if phase.goal}
				<p class="mt-2 text-sm text-text-secondary">{phase.goal}</p>
			{/if}
		</div>

		{#if renderedCriteria}
			<div class="mb-6 rounded-lg border border-border bg-surface p-4">
				<h2 class="mb-2 text-sm font-semibold text-text-primary">Success Criteria</h2>
				<div class="prose prose-invert prose-sm max-w-none text-text-secondary">
					{@html renderedCriteria}
				</div>
			</div>
		{/if}

		<div>
			<h2 class="mb-3 text-sm font-semibold text-text-primary">
				Tasks ({tasksQuery.data?.length || 0})
			</h2>
			{#if tasksQuery.isLoading}
				<p class="text-text-tertiary">Loading tasks...</p>
			{:else if tasksQuery.data && tasksQuery.data.length > 0}
				<div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
					{#each tasksQuery.data as task}
						<BoardCard {task} onclick={handleTaskClick} />
					{/each}
				</div>
			{:else}
				<p class="text-sm text-text-tertiary">No tasks in this phase</p>
			{/if}
		</div>
	{/if}
</div>

{#if selectedTask}
	<TaskDetail
		task={selectedTask}
		onclose={() => (selectedTask = null)}
		onupdate={handleTaskUpdate}
		ondelete={() => { selectedTask = null; queryClient.invalidateQueries({ queryKey: ['tasks', { phaseId: id }] }); }}
	/>
{/if}
