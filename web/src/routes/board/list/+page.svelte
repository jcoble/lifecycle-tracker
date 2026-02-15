<script lang="ts">
	import type { Task, TaskStatus } from '$lib/types';
	import TaskListCard from '$lib/components/board/TaskListCard.svelte';
	import FilterBar from '$lib/components/board/FilterBar.svelte';
	import TaskDetail from '$lib/components/tasks/TaskDetail.svelte';
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import { phases as phasesApi } from '$lib/api/endpoints/phases';
	import { milestones as milestonesApi } from '$lib/api/endpoints/milestones';
	import { labels as labelsApi } from '$lib/api/endpoints/labels';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';
	import { goto } from '$app/navigation';
	import { Columns3 } from '@lucide/svelte';

	const queryClient = useQueryClient();

	const tasksQuery = createQuery(() => ({
		queryKey: ['tasks', getCurrentProjectId()],
		queryFn: () => tasksApi.list({ projectId: String(getCurrentProjectId()) }),
	}));

	const milestonesQuery = createQuery(() => ({
		queryKey: ['milestones', getCurrentProjectId()],
		queryFn: () => milestonesApi.list(getCurrentProjectId()),
	}));

	let allMilestones = $derived(milestonesQuery.data ?? []);

	const phasesQuery = createQuery(() => ({
		queryKey: ['phases', 'board', allMilestones.map(m => m.id)],
		queryFn: async () => {
			if (!allMilestones.length) return [];
			const results = await Promise.all(allMilestones.map(m => phasesApi.list(m.id)));
			return results.flat();
		},
		enabled: milestonesQuery.isSuccess && allMilestones.length > 0,
	}));

	const labelsQuery = createQuery(() => ({
		queryKey: ['labels', getCurrentProjectId()],
		queryFn: () => labelsApi.list(getCurrentProjectId()),
	}));

	let selectedTask = $state<Task | null>(null);
	let filters = $state<Record<string, string>>({});
	let statusFilter = $state<TaskStatus | 'All'>('All');

	const statusTabs: (TaskStatus | 'All')[] = ['All', 'Todo', 'InProgress', 'Review', 'Blocked', 'Backlog', 'Done', 'Cancelled'];
	const statusLabels: Record<string, string> = {
		All: 'All',
		Todo: 'Todo',
		InProgress: 'In Progress',
		Review: 'Review',
		Blocked: 'Blocked',
		Backlog: 'Backlog',
		Done: 'Done',
		Cancelled: 'Cancelled',
	};

	let filteredPhases = $derived.by(() => {
		if (!filters.milestone) return phasesQuery.data ?? [];
		const msId = Number(filters.milestone);
		return (phasesQuery.data ?? []).filter(p => p.milestoneId === msId);
	});

	let filteredTasks = $derived.by(() => {
		let result = tasksQuery.data ?? [];
		// Exclude Done/Cancelled by default unless specifically filtered
		if (statusFilter === 'All') {
			result = result.filter(t => t.status !== 'Done' && t.status !== 'Cancelled');
		} else {
			result = result.filter(t => t.status === statusFilter);
		}
		if (filters.search) {
			const s = filters.search.trim().toLowerCase();
			const idMatch = s.match(/^#?(\d+)$/);
			if (idMatch) {
				result = result.filter(t => t.id === Number(idMatch[1]));
			} else {
				result = result.filter(t =>
					t.title.toLowerCase().includes(s) || t.description?.toLowerCase().includes(s)
				);
			}
		}
		if (filters.milestone) {
			const milestonePhaseIds = new Set(filteredPhases.map(p => p.id));
			result = result.filter(t => t.phaseId && milestonePhaseIds.has(t.phaseId));
		}
		if (filters.phase) result = result.filter(t => t.phaseId === Number(filters.phase));
		if (filters.priority) result = result.filter(t => t.priority === filters.priority);
		if (filters.type) result = result.filter(t => t.type === filters.type);
		if (filters.source) result = result.filter(t => t.source === filters.source);
		if (filters.label) result = result.filter(t => t.labels?.some(l => l.id === Number(filters.label)));

		// Sort using filter bar selection, or fall back to contextual defaults
		const sort = filters.sortBy || 'board';
		const dir = (filters.sortDir === 'desc' ? -1 : 1);

		if (sort === 'board') {
			// Default contextual sort per status tab
			if (statusFilter === 'Done') {
				result.sort((a, b) => {
					const da = a.completedAt ? new Date(a.completedAt).getTime() : 0;
					const db = b.completedAt ? new Date(b.completedAt).getTime() : 0;
					return db - da;
				});
			} else if (statusFilter === 'Cancelled') {
				result.sort((a, b) => {
					const da = a.updatedAt ? new Date(a.updatedAt).getTime() : 0;
					const db = b.updatedAt ? new Date(b.updatedAt).getTime() : 0;
					return db - da;
				});
			} else {
				const priorityOrder: Record<string, number> = { P1: 1, P2: 2, P3: 3, P4: 4 };
				result.sort((a, b) => {
					const pa = priorityOrder[a.priority] ?? 99;
					const pb = priorityOrder[b.priority] ?? 99;
					if (pa !== pb) return pa - pb;
					return b.id - a.id;
				});
			}
		} else {
			result.sort((a, b) => {
				let va: any, vb: any;
				if (sort === 'id') { va = a.id; vb = b.id; }
				else if (sort === 'title') { va = a.title.toLowerCase(); vb = b.title.toLowerCase(); }
				else if (sort === 'priority') {
					const po: Record<string, number> = { P1: 1, P2: 2, P3: 3, P4: 4 };
					va = po[a.priority] ?? 99; vb = po[b.priority] ?? 99;
				}
				else if (sort === 'createdAt') { va = a.createdAt ?? ''; vb = b.createdAt ?? ''; }
				else if (sort === 'updatedAt') { va = a.updatedAt ?? ''; vb = b.updatedAt ?? ''; }
				else { va = a.id; vb = b.id; }

				if (va < vb) return -1 * dir;
				if (va > vb) return 1 * dir;
				return 0;
			});
		}
		return result;
	});

	function handleTaskUpdated() {
		queryClient.invalidateQueries({ queryKey: ['tasks'] });
	}

	async function handleArchiveCompleted() {
		await tasksApi.archiveCompleted({
			projectId: getCurrentProjectId(),
			completedPhasesOnly: true,
		});
		handleTaskUpdated();
	}
</script>

<svelte:head>
	<title>Task List - Lifecycle Tracker</title>
</svelte:head>

<div class="flex h-full flex-col">
	<!-- Filter bar with kanban toggle -->
	<div class="flex items-center">
		<div class="flex-1 min-w-0 overflow-x-auto">
			<FilterBar
				phases={filteredPhases}
				milestones={allMilestones}
				labels={labelsQuery.data ?? []}
				onchange={(f) => (filters = f)}
				onArchiveCompleted={handleArchiveCompleted}
			/>
		</div>
		<button
			onclick={() => goto('/board')}
			class="shrink-0 mr-2 flex h-8 items-center gap-1 rounded-md border border-border px-2 text-xs text-text-secondary hover:bg-surface-hover"
			title="Switch to Kanban view"
		>
			<Columns3 class="h-3.5 w-3.5" />
			<span class="hidden sm:inline">Kanban</span>
		</button>
	</div>

	<!-- Status filter tabs -->
	<div class="flex gap-1 border-b border-border px-3 py-1.5 overflow-x-auto">
		{#each statusTabs as tab}
			<button
				onclick={() => (statusFilter = tab)}
				class="shrink-0 rounded-md px-2.5 py-1 text-xs font-medium transition-colors
					{statusFilter === tab
					? 'bg-accent/20 text-accent'
					: 'text-text-tertiary hover:bg-surface-hover hover:text-text-secondary'}"
			>
				{statusLabels[tab]}
				{#if tab !== 'All'}
					{@const count = (tasksQuery.data ?? []).filter(t => t.status === tab).length}
					{#if count > 0}
						<span class="ml-1 text-[10px] opacity-60">{count}</span>
					{/if}
				{/if}
			</button>
		{/each}
	</div>

	<!-- Task list -->
	{#if tasksQuery.isLoading}
		<div class="flex flex-1 items-center justify-center">
			<p class="text-text-tertiary">Loading tasks...</p>
		</div>
	{:else if filteredTasks.length === 0}
		<div class="flex flex-1 items-center justify-center">
			<p class="text-text-tertiary">No tasks found</p>
		</div>
	{:else}
		<div class="flex-1 space-y-2 overflow-y-auto p-3">
			{#each filteredTasks as task (task.id)}
				<TaskListCard {task} onclick={(t) => (selectedTask = t)} />
			{/each}
		</div>
	{/if}
</div>

<!-- Task detail slide-in -->
{#if selectedTask}
	<TaskDetail
		task={selectedTask}
		phases={phasesQuery.data ?? []}
		onclose={() => (selectedTask = null)}
		onupdate={(updated) => { selectedTask = updated; handleTaskUpdated(); }}
		ondelete={() => { selectedTask = null; handleTaskUpdated(); }}
	/>
{/if}
