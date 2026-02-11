<script lang="ts">
	import type { Task } from '$lib/types';
	import PriorityBadge from '$lib/components/shared/PriorityBadge.svelte';
	import AIBadge from '$lib/components/shared/AIBadge.svelte';
	import TestStatusBadge from '$lib/components/shared/TestStatusBadge.svelte';
	import TestingRequirementBadge from '$lib/components/testing/TestingRequirementBadge.svelte';
	import { formatDate } from '$lib/utils/date';
	import { pasteScreenshotToTask } from '$lib/utils/clipboard';
	import { extractPrLabel } from '$lib/utils/git';
	import { Calendar, ClipboardPaste, GitBranch, GitPullRequest, Bot, Play, Square } from '@lucide/svelte';
	import { taskAgentMap } from '$lib/stores/agentActivity';
	import { activeTestAgentTasks } from '$lib/stores/testAgents';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';

	let { task, onclick }: { task: Task; onclick?: (task: Task) => void } = $props();
	let assignedAgent = $derived($taskAgentMap.get(task.id));
	let hasActiveTestAgent = $derived($activeTestAgentTasks.has(task.id));
	let showPaste = $state(false);
	let spawnBusy = $state(false);
	let stopBusy = $state(false);

	async function handlePaste(e: MouseEvent) {
		e.stopPropagation();
		await pasteScreenshotToTask(task.id);
	}

	async function handleRunTests(e: MouseEvent) {
		e.stopPropagation();
		if (spawnBusy) return;
		spawnBusy = true;
		try {
			await tasksApi.spawnTestAgent(task.id);
		} catch {
			// silent — detail panel shows full errors
		} finally {
			spawnBusy = false;
		}
	}

	async function handleStopAgent(e: MouseEvent) {
		e.stopPropagation();
		if (stopBusy) return;
		stopBusy = true;
		try {
			await tasksApi.stopAgent(task.id);
		} catch {
			// silent
		} finally {
			stopBusy = false;
		}
	}

	let showTestButton = $derived(
		task.status === 'Review' || task.status === 'InProgress' || task.type === 'Test'
	);
</script>

<button
	class="group relative w-full cursor-pointer overflow-hidden rounded-lg border border-border bg-surface p-3 text-left transition-all hover:border-border-hover hover:bg-surface-hover"
	onclick={() => onclick?.(task)}
	onmouseenter={() => (showPaste = true)}
	onmouseleave={() => (showPaste = false)}
>
	<!-- Paste button (hover-visible) -->
	{#if showPaste}
		<span
			class="absolute top-1.5 right-1.5 rounded p-0.5 text-text-tertiary transition-colors hover:bg-accent/20 hover:text-accent"
			role="button"
			tabindex="-1"
			title="Paste screenshot"
			onclick={handlePaste}
		>
			<ClipboardPaste class="h-3 w-3" />
		</span>
	{/if}

	<!-- Top row: priority + AI badge + actions + ID -->
	<div class="mb-1.5 flex items-center gap-2">
		<PriorityBadge priority={task.priority} />
		{#if task.source === 'Claude'}
			<AIBadge />
		{/if}
		<div class="ml-auto flex items-center gap-1.5">
			{#if assignedAgent}
				<span
					class="rounded p-0.5 text-red-400 transition-colors hover:bg-red-500/20 hover:text-red-500 {stopBusy ? 'opacity-50' : ''}"
					role="button"
					tabindex="-1"
					title="Stop Agent"
					onclick={handleStopAgent}
				>
					<Square class="h-3.5 w-3.5" />
				</span>
			{:else if showTestButton}
				<span
					class="rounded p-0.5 text-text-tertiary transition-colors hover:bg-green-500/20 hover:text-green-400 {spawnBusy ? 'animate-pulse opacity-50' : ''}"
					role="button"
					tabindex="-1"
					title="Run Tests"
					onclick={handleRunTests}
				>
					<Play class="h-3.5 w-3.5" />
				</span>
			{/if}
			<span class="font-mono text-sm font-bold tabular-nums text-text-secondary">#{task.id}</span>
		</div>
	</div>

	<!-- Title -->
	<p class="mb-2 break-words text-sm font-medium leading-snug text-text-primary">{task.title}</p>

	<!-- Labels -->
	{#if task.labels && task.labels.length > 0}
		<div class="mb-2 flex flex-wrap gap-1">
			{#each task.labels as label}
				<span
					class="inline-block rounded-full px-1.5 py-0.5 text-[10px] font-medium"
					style="background: {label.color}20; color: {label.color};"
				>
					{label.name}
				</span>
			{/each}
		</div>
	{/if}

	<!-- Test agent running indicator -->
	{#if hasActiveTestAgent && !assignedAgent}
		<div class="mb-1.5 flex items-center gap-1.5">
			<span class="relative flex h-2 w-2">
				<span class="absolute inline-flex h-full w-full animate-ping rounded-full bg-green-400 opacity-75"></span>
				<span class="relative inline-flex h-2 w-2 rounded-full bg-green-500"></span>
			</span>
			<span class="text-[10px] font-medium text-green-400">Testing</span>
		</div>
	{/if}

	<!-- Agent working indicator -->
	{#if assignedAgent}
		<div class="mb-1.5 flex items-center gap-1.5">
			<span class="relative flex h-2 w-2">
				<span class="absolute inline-flex h-full w-full animate-ping rounded-full bg-green-400 opacity-75"></span>
				<span class="relative inline-flex h-2 w-2 rounded-full bg-green-500"></span>
			</span>
			<span class="flex items-center gap-1 text-[10px] font-medium text-text-secondary">
				<Bot class="h-2.5 w-2.5" />
				{assignedAgent.agentName}
			</span>
			{#if assignedAgent.currentActivity}
				<span class="truncate text-[10px] text-text-tertiary italic max-w-[120px]" title={assignedAgent.currentActivity}>
					{assignedAgent.currentActivity}
				</span>
			{/if}
		</div>
	{/if}

	<!-- Bottom row: tests + phase + due date -->
	<div class="flex min-w-0 items-center gap-2 text-text-tertiary">
		<!-- Tests -->
		{#if task.tests && task.tests.length > 0}
			<div class="flex items-center gap-1">
				{#each task.tests as test}
					<TestStatusBadge testType={test.type} status={test.status} />
				{/each}
			</div>
		{/if}

		{#if task.requiredTestLevel}
			<TestingRequirementBadge requiredLevel={task.requiredTestLevel} testPlans={task.testPlans || []} compact />
		{/if}

		{#if task.phaseName}
			<span class="truncate text-[10px]">{task.phaseName}</span>
		{/if}

		{#if task.gitBranch}
			<span class="flex items-center gap-0.5 text-[10px] font-mono truncate max-w-[100px]"
				title={task.gitBranch}>
				<GitBranch class="h-2.5 w-2.5 shrink-0" />
				{task.gitBranch.split('/').pop()}
			</span>
		{/if}

		{#if task.pullRequestUrl}
			<a href={task.pullRequestUrl} target="_blank" rel="noopener"
				class="flex items-center gap-0.5 text-[10px] text-accent hover:underline"
				title="Pull Request"
				onclick={(e) => e.stopPropagation()}>
				<GitPullRequest class="h-2.5 w-2.5 shrink-0" />
				{extractPrLabel(task.pullRequestUrl)}
			</a>
		{/if}

		{#if task.dueDate}
			<span class="ml-auto flex items-center gap-0.5 text-[10px]">
				<Calendar class="h-2.5 w-2.5" />
				{formatDate(task.dueDate)}
			</span>
		{/if}
	</div>
</button>
