<script lang="ts">
	import type { TaskPriority, TaskType, TaskSource, Phase, Label, Milestone } from '$lib/types';
	import { Archive, Search, X } from '@lucide/svelte';

	let {
		search = '',
		milestone = '',
		phase = '',
		priority = '',
		type = '',
		source = '',
		label = '',
		sortBy = 'board',
		sortDir = 'asc',
		phases = [] as Phase[],
		milestones = [] as Milestone[],
		labels = [] as Label[],
		onchange,
		onArchiveCompleted,
	}: {
		search?: string;
		milestone?: string;
		phase?: string;
		priority?: string;
		type?: string;
		source?: string;
		label?: string;
		sortBy?: string;
		sortDir?: 'asc' | 'desc';
		phases?: Phase[];
		milestones?: Milestone[];
		labels?: Label[];
		onchange?: (filters: Record<string, string>) => void;
		onArchiveCompleted?: () => Promise<void> | void;
	} = $props();

	let archiveBusy = $state(false);

	function emit() {
		onchange?.({ search, milestone, phase, priority, type, source, label, sortBy, sortDir });
	}

	function handleMilestoneChange() {
		phase = ''; // Reset phase when milestone changes
		emit();
	}

	function clearAll() {
		search = '';
		milestone = '';
		phase = '';
		priority = '';
		type = '';
		source = '';
		label = '';
		sortBy = 'board';
		sortDir = 'asc';
		emit();
	}

	async function handleArchiveCompleted() {
		if (archiveBusy) return;
		archiveBusy = true;
		try {
			await onArchiveCompleted?.();
		} finally {
			archiveBusy = false;
		}
	}

	let hasFilters = $derived(
		!!search || !!milestone || !!phase || !!priority || !!type || !!source || !!label || sortBy !== 'board' || sortDir !== 'asc'
	);

	const priorities: TaskPriority[] = ['P1', 'P2', 'P3', 'P4'];
	const types: TaskType[] = ['Feature', 'Bug', 'Refactor', 'Docs', 'Test', 'Infra', 'Research'];
	const sources: TaskSource[] = ['Manual', 'Claude'];
	const sortFields = [
		{ value: 'board', label: 'Board Order' },
		{ value: 'id', label: 'Task Number' },
		{ value: 'title', label: 'Title' },
		{ value: 'description', label: 'Description' },
		{ value: 'priority', label: 'Priority' },
		{ value: 'createdAt', label: 'Created' },
		{ value: 'updatedAt', label: 'Updated' },
	];
</script>

<div class="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2 sm:px-4 overflow-x-auto">
	<!-- Search -->
	<div class="relative min-w-0 flex-shrink-0">
		<Search class="absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-text-tertiary" />
		<input
			type="text"
			bind:value={search}
			oninput={emit}
			placeholder="Search or #task..."
			class="h-8 w-40 sm:w-48 rounded-md border border-border bg-surface pl-8 pr-3 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none"
		/>
	</div>

	<!-- Milestone -->
	{#if milestones.length > 0}
		<select
			bind:value={milestone}
			onchange={handleMilestoneChange}
			class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
		>
			<option value="">All Milestones</option>
			{#each milestones as m}
				<option value={String(m.id)}>
					{m.name}{m.status === 'InProgress' ? ' *' : ''}
				</option>
			{/each}
		</select>
	{/if}

	<!-- Phase -->
	{#if phases.length > 0}
		<select
			bind:value={phase}
			onchange={emit}
			class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
		>
			<option value="">All Phases</option>
			{#each phases as p}
				<option value={String(p.id)}>{p.name}</option>
			{/each}
		</select>
	{/if}

	<!-- Priority -->
	<select
		bind:value={priority}
		onchange={emit}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
	>
		<option value="">All Priorities</option>
		{#each priorities as p}
			<option value={p}>{p}</option>
		{/each}
	</select>

	<!-- Type -->
	<select
		bind:value={type}
		onchange={emit}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
	>
		<option value="">All Types</option>
		{#each types as t}
			<option value={t}>{t}</option>
		{/each}
	</select>

	<!-- Source -->
	<select
		bind:value={source}
		onchange={emit}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
	>
		<option value="">All Sources</option>
		{#each sources as s}
			<option value={s}>{s}</option>
		{/each}
	</select>

	<!-- Label -->
	{#if labels.length > 0}
		<select
			bind:value={label}
			onchange={emit}
			class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
		>
			<option value="">All Labels</option>
			{#each labels as l}
				<option value={String(l.id)}>{l.name}</option>
			{/each}
		</select>
	{/if}

	<!-- Sort -->
	<select
		bind:value={sortBy}
		onchange={emit}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none"
	>
		{#each sortFields as field}
			<option value={field.value}>{field.label}</option>
		{/each}
	</select>

	<select
		bind:value={sortDir}
		onchange={emit}
		disabled={sortBy === 'board'}
		class="h-8 rounded-md border border-border bg-surface px-2 text-sm text-text-secondary focus:border-accent focus:outline-none disabled:opacity-50"
	>
		<option value="asc">Ascending</option>
		<option value="desc">Descending</option>
	</select>

	<button
		onclick={handleArchiveCompleted}
		disabled={archiveBusy}
		class="flex h-8 items-center gap-1 rounded-md border border-border px-2 text-xs text-text-secondary transition-colors hover:bg-surface-hover disabled:opacity-50"
		title="Archive done tasks in completed phases"
	>
		<Archive class="h-3.5 w-3.5" />
		{archiveBusy ? 'Archiving...' : 'Archive Completed'}
	</button>

	<!-- Clear -->
	{#if hasFilters}
		<button
			onclick={clearAll}
			class="flex items-center gap-1 rounded-md px-2 py-1 text-xs text-text-tertiary hover:bg-surface-hover hover:text-text-secondary"
		>
			<X class="h-3 w-3" />
			Clear
		</button>
	{/if}
</div>
