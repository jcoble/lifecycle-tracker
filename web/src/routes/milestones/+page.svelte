<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { milestones as milestonesApi } from '$lib/api/endpoints/milestones';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { formatDate } from '$lib/utils/date';
	import type { Milestone } from '$lib/types';
	import { Target, Calendar } from '@lucide/svelte';

	const milestonesQuery = createQuery(() => ({
		queryKey: ['milestones'],
		queryFn: () => milestonesApi.list(1),
	}));

	let grouped = $derived.by(() => {
		const data = milestonesQuery.data || [];
		const groups: Record<string, Milestone[]> = {
			Active: [],
			Planning: [],
			Completed: [],
			Other: [],
		};
		for (const m of data) {
			if (m.status === 'InProgress') groups.Active.push(m);
			else if (m.status === 'Planning') groups.Planning.push(m);
			else if (m.status === 'Completed') groups.Completed.push(m);
			else groups.Other.push(m);
		}
		return groups;
	});

	let selectedMilestoneId = $state<number | null>(null);
</script>

<svelte:head>
	<title>Milestones - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-6">
		<h1 class="text-2xl font-bold text-text-primary">Milestones</h1>
	</div>

	{#if milestonesQuery.isLoading}
		<p class="text-text-tertiary">Loading milestones...</p>
	{:else}
		{#each Object.entries(grouped) as [group, items]}
			{#if items.length > 0}
				<div class="mb-6">
					<h2 class="mb-3 text-sm font-semibold uppercase tracking-wider text-text-tertiary">{group}</h2>
					<div class="space-y-3">
						{#each items as milestone}
							<button
								class="w-full rounded-lg border border-border bg-surface p-4 text-left transition-colors hover:border-border-hover"
								onclick={() => (selectedMilestoneId = selectedMilestoneId === milestone.id ? null : milestone.id)}
							>
								<div class="flex items-start justify-between">
									<div class="flex items-center gap-3">
										<Target class="h-5 w-5 shrink-0 text-text-tertiary" />
										<div>
											<h3 class="font-medium text-text-primary">{milestone.name}</h3>
											{#if milestone.version}
												<span class="text-xs text-text-tertiary">v{milestone.version}</span>
											{/if}
										</div>
									</div>
									<StatusBadge status={milestone.status} />
								</div>

								{#if milestone.description}
									<p class="mt-2 pl-8 text-sm text-text-secondary">{milestone.description}</p>
								{/if}

								<div class="mt-3 flex items-center gap-4 pl-8 text-xs text-text-tertiary">
									{#if milestone.targetDate}
										<span class="flex items-center gap-1">
											<Calendar class="h-3 w-3" />
											Target: {formatDate(milestone.targetDate)}
										</span>
									{/if}
									{#if milestone.phases}
										{@const completedPhases = milestone.phases.filter((p) => p.status === 'Completed').length}
										<span>{completedPhases}/{milestone.phases.length} phases completed</span>
									{/if}
								</div>

								{#if selectedMilestoneId === milestone.id && milestone.phases}
									<div class="mt-4 space-y-2 border-t border-border pl-8 pt-4">
										<h4 class="text-xs font-semibold uppercase text-text-tertiary">Phases</h4>
										{#each milestone.phases as phase}
											<a
												href="/phases/{phase.id}"
												class="flex items-center justify-between rounded-md border border-border bg-bg p-2 transition-colors hover:border-border-hover"
											>
												<span class="text-sm text-text-primary">{phase.name}</span>
												<StatusBadge status={phase.status} />
											</a>
										{/each}
									</div>
								{/if}
							</button>
						{/each}
					</div>
				</div>
			{/if}
		{/each}
	{/if}
</div>
