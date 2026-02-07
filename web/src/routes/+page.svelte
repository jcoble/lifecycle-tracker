<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { projects } from '$lib/api/endpoints/projects';
	import type { Dashboard } from '$lib/types';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import ActivityFeed from '$lib/components/shared/ActivityFeed.svelte';
	import {
		CheckCircle,
		ListTodo,
		FlaskConical,
		Zap,
		Plus,
		ArrowRight
	} from '@lucide/svelte';

	const dashboardQuery = createQuery(() => ({
		queryKey: ['dashboard'],
		queryFn: () => projects.dashboard(1),
	}));
</script>

<svelte:head>
	<title>Dashboard - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	{#if dashboardQuery.isLoading}
		<div class="flex h-64 items-center justify-center">
			<p class="text-text-tertiary">Loading dashboard...</p>
		</div>
	{:else if dashboardQuery.isError}
		<div class="flex h-64 items-center justify-center">
			<p class="text-danger">Failed to load dashboard</p>
		</div>
	{:else if dashboardQuery.data}
		{@const data = dashboardQuery.data as Dashboard}
		<!-- Header -->
		<div class="mb-6">
			<h1 class="text-2xl font-bold text-text-primary">{data.project.name}</h1>
			{#if data.project.description}
				<p class="mt-1 text-sm text-text-secondary">{data.project.description}</p>
			{/if}
		</div>

		<!-- Stats cards -->
		<div class="mb-8 grid grid-cols-2 gap-4 lg:grid-cols-4">
			<div class="rounded-lg border border-border bg-surface p-4">
				<div class="flex items-center gap-2 text-text-tertiary">
					<ListTodo class="h-4 w-4" />
					<span class="text-xs">Total Tasks</span>
				</div>
				<p class="mt-2 text-2xl font-bold text-text-primary">{data.taskSummary.total}</p>
			</div>
			<div class="rounded-lg border border-border bg-surface p-4">
				<div class="flex items-center gap-2 text-success">
					<CheckCircle class="h-4 w-4" />
					<span class="text-xs">Completed</span>
				</div>
				<p class="mt-2 text-2xl font-bold text-text-primary">{data.taskSummary.byStatus['Done'] ?? 0}</p>
			</div>
			<div class="rounded-lg border border-border bg-surface p-4">
				<div class="flex items-center gap-2 text-text-tertiary">
					<FlaskConical class="h-4 w-4" />
					<span class="text-xs">Tests Passing</span>
				</div>
				<p class="mt-2 text-2xl font-bold text-text-primary">{data.testSummary.passing}</p>
			</div>
			<div class="rounded-lg border border-border bg-surface p-4">
				<div class="flex items-center gap-2 text-purple-400">
					<Zap class="h-4 w-4" />
					<span class="text-xs">AI Created</span>
				</div>
				<p class="mt-2 text-2xl font-bold text-text-primary">{data.taskSummary.bySource?.['Claude'] ?? 0}</p>
			</div>
		</div>

		<div class="grid gap-6 lg:grid-cols-3">
			<!-- Active Milestone -->
			<div class="lg:col-span-2 space-y-6">
				{#if data.activeMilestone}
					{@const totalPhases = data.phases.length}
					{@const completedPhases = data.phases.filter((p) => p.status === 'Completed').length}
					<div class="rounded-lg border border-border bg-surface p-5">
						<div class="mb-3 flex items-center justify-between">
							<h2 class="font-semibold text-text-primary">Active Milestone</h2>
							<StatusBadge status={data.activeMilestone.status} />
						</div>
						<p class="mb-1 text-sm text-text-primary">{data.activeMilestone.name}</p>
						{#if data.activeMilestone.version}
							<p class="mb-3 text-xs text-text-tertiary">v{data.activeMilestone.version}</p>
						{/if}
						<div class="mb-1 flex justify-between text-xs text-text-tertiary">
							<span>Phases</span>
							<span>{completedPhases}/{totalPhases}</span>
						</div>
						<div class="h-2 rounded-full bg-surface-hover">
							<div
								class="h-2 rounded-full bg-accent transition-all"
								style="width: {totalPhases > 0 ? (completedPhases / totalPhases) * 100 : 0}%"
							></div>
						</div>
					</div>
				{/if}

				<!-- Active Phases -->
				{#if data.phases.length > 0}
					<div>
						<h2 class="mb-3 font-semibold text-text-primary">Active Phases</h2>
						<div class="grid gap-3 sm:grid-cols-2">
							{#each data.phases.filter((p) => p.status !== 'Completed' && p.status !== 'Cancelled') as phase}
								{@const total = phase.taskCount || 0}
								{@const completed = phase.doneCount || 0}
								<a
									href="/phases/{phase.id}"
									class="group rounded-lg border border-border bg-surface p-4 transition-colors hover:border-border-hover"
								>
									<div class="mb-2 flex items-center justify-between">
										<h3 class="text-sm font-medium text-text-primary group-hover:text-accent">{phase.name}</h3>
										<StatusBadge status={phase.status} />
									</div>
									{#if phase.goal}
										<p class="mb-3 text-xs text-text-secondary line-clamp-2">{phase.goal}</p>
									{/if}
									<div class="flex items-center justify-between text-xs text-text-tertiary">
										<span>{completed}/{total} tasks</span>
										<ArrowRight class="h-3 w-3 opacity-0 transition-opacity group-hover:opacity-100" />
									</div>
									{#if total > 0}
										<div class="mt-1.5 h-1 rounded-full bg-surface-hover">
											<div
												class="h-1 rounded-full bg-accent"
												style="width: {(completed / total) * 100}%"
											></div>
										</div>
									{/if}
								</a>
							{/each}
						</div>
					</div>
				{/if}
			</div>

			<!-- Sidebar: Activity + Quick Actions -->
			<div class="space-y-6">
				<!-- Quick actions -->
				<div class="rounded-lg border border-border bg-surface p-4">
					<h2 class="mb-3 text-sm font-semibold text-text-primary">Quick Actions</h2>
					<div class="space-y-2">
						<a
							href="/board"
							class="flex w-full items-center gap-2 rounded-md border border-border px-3 py-2 text-sm text-text-secondary transition-colors hover:bg-surface-hover hover:text-text-primary"
						>
							<Plus class="h-3.5 w-3.5" />
							New Task
						</a>
					</div>
				</div>

				<!-- Recent activity -->
				<div class="rounded-lg border border-border bg-surface p-4">
					<h2 class="mb-3 text-sm font-semibold text-text-primary">Recent Activity</h2>
					<ActivityFeed activities={data.recentActivity} compact />
				</div>
			</div>
		</div>
	{/if}
</div>
