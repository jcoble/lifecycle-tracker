<script lang="ts">
	import type { Task } from '$lib/types';
	import PriorityBadge from '$lib/components/shared/PriorityBadge.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import AIBadge from '$lib/components/shared/AIBadge.svelte';
	import { Bot, GitPullRequest } from '@lucide/svelte';
	import { taskAgentMap } from '$lib/stores/agentActivity';

	let { task, onclick }: { task: Task; onclick?: (task: Task) => void } = $props();
	let assignedAgent = $derived($taskAgentMap.get(task.id));
</script>

<button
	class="w-full rounded-lg border border-border bg-surface p-3 text-left transition-colors hover:border-border-hover hover:bg-surface-hover active:bg-surface-hover"
	onclick={() => onclick?.(task)}
>
	<!-- Row 1: priority + ID + title -->
	<div class="flex items-center gap-2">
		<PriorityBadge priority={task.priority} />
		<span class="shrink-0 font-mono text-xs font-bold tabular-nums text-text-secondary">#{task.id}</span>
		<p class="min-w-0 flex-1 truncate text-sm font-medium text-text-primary">{task.title}</p>
	</div>

	<!-- Row 2: status + type + labels + PR -->
	<div class="mt-1.5 flex flex-wrap items-center gap-1.5 text-[11px]">
		<StatusBadge status={task.status} />
		<span class="rounded-full bg-surface-hover px-1.5 py-0.5 text-text-tertiary">{task.type}</span>
		{#if task.source === 'Claude'}
			<AIBadge />
		{/if}
		{#if task.labels && task.labels.length > 0}
			{#each task.labels.slice(0, 2) as label}
				<span
					class="rounded-full px-1.5 py-0.5 text-[10px] font-medium"
					style="background: {label.color}20; color: {label.color};"
				>
					{label.name}
				</span>
			{/each}
			{#if task.labels.length > 2}
				<span class="text-text-tertiary">+{task.labels.length - 2}</span>
			{/if}
		{/if}
		{#if task.pullRequestUrl}
			<a href={task.pullRequestUrl} target="_blank" rel="noopener"
				class="flex items-center gap-0.5 text-accent hover:underline"
				onclick={(e) => e.stopPropagation()}>
				<GitPullRequest class="h-3 w-3" />
				PR
			</a>
		{/if}
	</div>

	<!-- Row 3: agent indicator (if active) -->
	{#if assignedAgent}
		<div class="mt-1.5 flex items-center gap-1.5">
			<span class="relative flex h-2 w-2">
				<span class="absolute inline-flex h-full w-full animate-ping rounded-full bg-green-400 opacity-75"></span>
				<span class="relative inline-flex h-2 w-2 rounded-full bg-green-500"></span>
			</span>
			<Bot class="h-3 w-3 text-text-tertiary" />
			<span class="text-[10px] text-text-secondary">{assignedAgent.agentName}</span>
			{#if assignedAgent.currentActivity}
				<span class="truncate text-[10px] text-text-tertiary italic" title={assignedAgent.currentActivity}>
					{assignedAgent.currentActivity}
				</span>
			{/if}
		</div>
	{/if}
</button>
