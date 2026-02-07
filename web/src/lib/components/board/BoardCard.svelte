<script lang="ts">
	import type { Task } from '$lib/types';
	import PriorityBadge from '$lib/components/shared/PriorityBadge.svelte';
	import AIBadge from '$lib/components/shared/AIBadge.svelte';
	import TestStatusBadge from '$lib/components/shared/TestStatusBadge.svelte';
	import TestingRequirementBadge from '$lib/components/testing/TestingRequirementBadge.svelte';
	import { formatDate } from '$lib/utils/date';
	import { pasteScreenshotToTask } from '$lib/utils/clipboard';
	import { Calendar, ClipboardPaste } from '@lucide/svelte';

	let { task, onclick }: { task: Task; onclick?: (task: Task) => void } = $props();
	let showPaste = $state(false);

	async function handlePaste(e: MouseEvent) {
		e.stopPropagation();
		await pasteScreenshotToTask(task.id);
	}
</script>

<button
	class="group relative w-full cursor-pointer rounded-lg border border-border bg-surface p-3 text-left transition-all hover:border-border-hover hover:bg-surface-hover"
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

	<!-- Top row: priority + AI badge + ID -->
	<div class="mb-1.5 flex items-center gap-2">
		<PriorityBadge priority={task.priority} />
		{#if task.source === 'Claude'}
			<AIBadge />
		{/if}
		<span class="ml-auto font-mono text-[10px] text-text-tertiary">#{task.id}</span>
	</div>

	<!-- Title -->
	<p class="mb-2 text-sm font-medium leading-snug text-text-primary">{task.title}</p>

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

	<!-- Bottom row: tests + phase + due date -->
	<div class="flex items-center gap-2 text-text-tertiary">
		<!-- Tests -->
		{#if task.tests && task.tests.length > 0}
			<div class="flex items-center gap-1">
				{#each task.tests as test}
					<TestStatusBadge testType={test.testType} status={test.status} />
				{/each}
			</div>
		{/if}

		{#if task.requiredTestLevel}
			<TestingRequirementBadge requiredLevel={task.requiredTestLevel} testPlans={task.testPlans || []} compact />
		{/if}

		{#if task.phaseName}
			<span class="truncate text-[10px]">{task.phaseName}</span>
		{/if}

		{#if task.dueDate}
			<span class="ml-auto flex items-center gap-0.5 text-[10px]">
				<Calendar class="h-2.5 w-2.5" />
				{formatDate(task.dueDate)}
			</span>
		{/if}
	</div>
</button>
