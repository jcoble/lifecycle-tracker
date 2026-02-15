<script lang="ts">
	import type { TestPlan, TestStepResultItem } from '$lib/types';
	import { Play, Eye, CheckCircle, XCircle, Clock, FileText, ChevronDown, ChevronRight, Camera } from '@lucide/svelte';
	import { api } from '$lib/api/client';

	let {
		plan,
		onexecute,
		onview,
	}: {
		plan: TestPlan;
		onexecute?: (planId: number) => void;
		onview?: (planId: number) => void;
	} = $props();

	let expandedTests = $state<Set<number>>(new Set());
	let stepResults = $state<Map<number, TestStepResultItem>>(new Map());
	let resultsLoaded = $state(false);

	async function loadStepResults() {
		if (resultsLoaded || !plan.latestExecution) return;
		try {
			const exec = await api.get<{ stepResults?: TestStepResultItem[] }>(`/test-executions/${plan.latestExecution.id}`);
			if (exec.stepResults) {
				const map = new Map<number, TestStepResultItem>();
				for (const r of exec.stepResults) map.set(r.testStepId, r);
				stepResults = map;
			}
		} catch { /* best effort */ }
		resultsLoaded = true;
	}

	const levelColors: Record<string, string> = {
		Smoke: 'bg-blue-500/10 text-blue-400',
		Comprehensive: 'bg-orange-500/10 text-orange-400',
		FullE2E: 'bg-red-500/10 text-red-400',
	};

	const statusIcons: Record<string, typeof CheckCircle> = {
		Draft: FileText,
		ReadyForExecution: Clock,
		InProgress: Clock,
		Passing: CheckCircle,
		Failing: XCircle,
	};

	const statusColors: Record<string, string> = {
		Draft: 'text-text-tertiary',
		ReadyForExecution: 'text-warning',
		InProgress: 'text-accent',
		Passing: 'text-success',
		Failing: 'text-danger',
	};

	const typeColors: Record<string, string> = {
		Unit: 'bg-blue-500/10 text-blue-400',
		Integration: 'bg-purple-500/10 text-purple-400',
		UI: 'bg-orange-500/10 text-orange-400',
		Manual: 'bg-gray-500/10 text-gray-400',
	};

	const testStatusColors: Record<string, string> = {
		Passing: 'text-success',
		Failing: 'text-danger',
		Created: 'text-warning',
		NotCreated: 'text-text-tertiary',
		Skipped: 'text-text-tertiary',
	};

	function toggleTest(testId: number) {
		const next = new Set(expandedTests);
		if (next.has(testId)) next.delete(testId);
		else {
			next.add(testId);
			loadStepResults();
		}
		expandedTests = next;
	}
</script>

<div class="rounded-lg border border-border bg-surface p-3">
	<div class="flex items-start justify-between gap-2">
		<div class="min-w-0 flex-1">
			<div class="mb-1 flex items-center gap-2">
				<span class="rounded-full px-2 py-0.5 text-[10px] font-medium {levelColors[plan.requiredLevel] || ''}">
					{plan.requiredLevel}
				</span>
				{#if statusIcons[plan.status]}
					{@const StatusIcon = statusIcons[plan.status]}
					<span class="flex items-center gap-1 text-xs {statusColors[plan.status] || ''}">
						<StatusIcon class="h-3 w-3" />
						{plan.status}
					</span>
				{:else}
					<span class="flex items-center gap-1 text-xs text-text-tertiary">
						<FileText class="h-3 w-3" />
						{plan.status}
					</span>
				{/if}
				<span class="text-[10px] text-text-tertiary">
					{plan.source === 'AI_Generated' ? 'AI' : plan.source}
				</span>
			</div>
			<p class="text-sm font-medium text-text-primary truncate">{plan.name}</p>
			{#if plan.description}
				<p class="mt-0.5 text-xs text-text-tertiary truncate">{plan.description}</p>
			{/if}
		</div>

		<div class="flex items-center gap-1 shrink-0">
			{#if onview}
				<button
					onclick={() => onview?.(plan.id)}
					class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
					title="View details"
				>
					<Eye class="h-3.5 w-3.5" />
				</button>
			{/if}
			{#if onexecute && plan.status !== 'InProgress'}
				<button
					onclick={() => onexecute?.(plan.id)}
					class="rounded p-1 text-accent transition-colors hover:bg-accent/10"
					title="Execute test plan"
				>
					<Play class="h-3.5 w-3.5" />
				</button>
			{/if}
		</div>
	</div>

	<!-- Tests breakdown -->
	{#if plan.tests && plan.tests.length > 0}
		<div class="mt-2 space-y-1">
			{#each plan.tests as test}
				<div class="rounded-md border border-border bg-bg">
					<button
						onclick={() => toggleTest(test.id)}
						class="flex w-full items-center gap-2 px-2 py-1 text-left"
					>
						{#if expandedTests.has(test.id)}
							<ChevronDown class="h-3 w-3 text-text-tertiary shrink-0" />
						{:else}
							<ChevronRight class="h-3 w-3 text-text-tertiary shrink-0" />
						{/if}
						<span class="rounded-full px-1.5 py-0.5 text-[9px] font-medium {typeColors[test.type] || ''}">
							{test.type}
						</span>
						<span class="flex-1 truncate text-xs text-text-primary">{test.name}</span>
						<span class="text-[10px] {testStatusColors[test.status] || 'text-text-tertiary'}">
							{#if test.totalRuns > 0}
								{test.passedRuns}/{test.totalRuns}
							{:else}
								{test.status}
							{/if}
						</span>
					</button>

					{#if expandedTests.has(test.id)}
						<div class="border-t border-border px-2 py-1.5 space-y-1">
							{#if test.testFile || test.framework}
								<div class="flex items-center gap-2 text-[10px] text-text-tertiary">
									{#if test.testFile}
										<span class="font-mono truncate">{test.testFile}</span>
									{/if}
									{#if test.framework}
										<span class="rounded-full bg-surface-hover px-1.5 py-0.5 shrink-0">{test.framework}</span>
									{/if}
								</div>
							{/if}
							{#if test.lastRunAt}
								<div class="flex items-center gap-2 text-[10px] text-text-tertiary">
									<span>Runs: {test.passedRuns} passed, {test.failedRuns} failed of {test.totalRuns}</span>
								</div>
							{/if}
							{#if test.steps && test.steps.length > 0}
								<div class="space-y-0.5">
									{#each test.steps as step, i}
										{@const result = stepResults.get(step.id)}
										<div class="flex items-center gap-1.5 text-[10px] text-text-tertiary">
											<span class="w-3 text-right shrink-0">{i + 1}.</span>
											{#if result}
												{#if result.status === 'Passed'}
													<CheckCircle class="h-2.5 w-2.5 text-success shrink-0" />
												{:else if result.status === 'Failed'}
													<XCircle class="h-2.5 w-2.5 text-danger shrink-0" />
												{:else}
													<Clock class="h-2.5 w-2.5 text-text-tertiary shrink-0" />
												{/if}
											{/if}
											<span class="truncate flex-1">{step.description}</span>
											{#if result?.screenshot && plan.latestExecution}
												<a
													href="/api/test-executions/{plan.latestExecution.id}/step-results/{result.id}/screenshot"
													target="_blank"
													rel="noopener"
													class="shrink-0 text-accent hover:text-accent-hover"
													title="View screenshot"
												>
													<Camera class="h-3 w-3" />
												</a>
											{/if}
										</div>
									{/each}
								</div>
							{:else}
								<p class="text-[10px] text-text-tertiary italic">No steps defined</p>
							{/if}
						</div>
					{/if}
				</div>
			{/each}
		</div>
	{/if}

	<!-- Execution stats -->
	{#if plan.latestExecution}
		{@const exec = plan.latestExecution}
		<div class="mt-2 flex items-center gap-3 text-[10px] text-text-tertiary">
			<span class="text-success">{exec.passedSteps} passed</span>
			{#if exec.failedSteps > 0}
				<span class="text-danger">{exec.failedSteps} failed</span>
			{/if}
			<span>{exec.totalSteps} total steps</span>
		</div>
	{:else if !plan.tests || plan.tests.length === 0}
		<p class="mt-2 text-[10px] text-text-tertiary">No tests - not yet executed</p>
	{/if}
</div>
