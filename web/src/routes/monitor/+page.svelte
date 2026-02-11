<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { team } from '$lib/api/endpoints/team';
	import { activity as activityApi } from '$lib/api/endpoints/activity';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';
	import type { MonitorAgent } from '$lib/types';
	import { Bot, Clock, Eye, Cpu, Zap, Activity, X } from '@lucide/svelte';

	let selectedPlan = $state<{ agentName: string; fileName: string; content: string; updatedAt: string } | null>(null);
	let loadingPlan = $state(false);

	const monitorQuery = createQuery(() => ({
		queryKey: ['monitor', getCurrentProjectId()],
		queryFn: () => team.getMonitor(getCurrentProjectId()),
		refetchInterval: 10000,
	}));

	const activityQuery = createQuery(() => ({
		queryKey: ['activity', getCurrentProjectId()],
		queryFn: () => activityApi.list(getCurrentProjectId(), { limit: '20' }),
		refetchInterval: 15000,
	}));

	function getModelBadge(model: string): { label: string; class: string } {
		if (model.includes('opus')) return { label: 'Opus', class: 'bg-purple-500/20 text-purple-400' };
		if (model.includes('sonnet')) return { label: 'Sonnet', class: 'bg-blue-500/20 text-blue-400' };
		if (model.includes('haiku')) return { label: 'Haiku', class: 'bg-green-500/20 text-green-400' };
		return { label: model, class: 'bg-gray-500/20 text-gray-400' };
	}

	function getStatusIndicator(agent: MonitorAgent): { class: string; pulse: boolean; label: string } {
		if (agent.isStale) return { class: 'bg-amber-500', pulse: false, label: 'Stale' };
		if (agent.status === 'Active') return { class: 'bg-green-500', pulse: true, label: 'Active' };
		return { class: 'bg-gray-500', pulse: false, label: 'Idle' };
	}

	/** Ensure UTC timestamps from the API (which lack Z suffix) parse correctly */
	function parseUtc(dateStr: string): Date {
		return new Date(dateStr.endsWith('Z') ? dateStr : dateStr + 'Z');
	}

	function formatDuration(start: string | null): string {
		if (!start) return '-';
		const ms = Date.now() - parseUtc(start).getTime();
		if (ms < 0) return 'just now';
		const mins = Math.floor(ms / 60000);
		if (mins < 1) return '<1m';
		if (mins < 60) return `${mins}m`;
		const hrs = Math.floor(mins / 60);
		return `${hrs}h ${mins % 60}m`;
	}

	function formatTokens(tokens: number | null): string {
		if (!tokens) return '-';
		if (tokens > 1000000) return `${(tokens / 1000000).toFixed(1)}M`;
		if (tokens > 1000) return `${(tokens / 1000).toFixed(1)}k`;
		return String(tokens);
	}

	function timeAgo(dateStr: string): string {
		const ms = Date.now() - parseUtc(dateStr).getTime();
		if (ms < 0) return 'just now';
		const secs = Math.floor(ms / 1000);
		if (secs < 60) return `${secs}s ago`;
		const mins = Math.floor(secs / 60);
		if (mins < 60) return `${mins}m ago`;
		const hrs = Math.floor(mins / 60);
		if (hrs < 24) return `${hrs}h ago`;
		const days = Math.floor(hrs / 24);
		return `${days}d ago`;
	}

	function getAgentActivity(agents?: MonitorAgent[]) {
		return (agents ?? [])
			.filter(a => a.currentActivity || a.sessionStarted)
			.map(a => ({
				agentName: a.agentName,
				role: a.role,
				activity: a.currentActivity,
				time: a.lastHeartbeat || a.sessionStarted,
				status: a.status,
				isStale: a.isStale,
			}))
			.sort((a, b) => {
				if (!a.time) return 1;
				if (!b.time) return -1;
				return parseUtc(b.time).getTime() - parseUtc(a.time).getTime();
			});
	}

	async function viewPlan(agent: MonitorAgent) {
		loadingPlan = true;
		try {
			const data = await team.getAgentPlan(agent.id);
			if (data.latestPlanContent) {
				selectedPlan = {
					agentName: agent.agentName,
					fileName: data.latestPlanFileName || 'plan.md',
					content: data.latestPlanContent,
					updatedAt: data.updatedAt || '',
				};
			}
		} catch {
			// ignore
		} finally {
			loadingPlan = false;
		}
	}
</script>

<svelte:head>
	<title>Monitor - Lifecycle</title>
</svelte:head>

<div class="h-full flex flex-col">
	<!-- Header -->
	<div class="border-b border-border bg-surface px-6 py-4">
		<div class="flex items-center justify-between">
			<div class="flex items-center gap-3">
				<Activity class="h-5 w-5 text-accent" />
				<h1 class="text-lg font-semibold text-text-primary">Agent Monitor</h1>
				{#if monitorQuery.data?.agents}
					{@const activeCount = monitorQuery.data.agents.filter(a => a.status === 'Active').length}
					{#if activeCount > 0}
						<span class="rounded-full bg-green-500/20 px-2.5 py-0.5 text-xs font-medium text-green-400">
							{activeCount} active
						</span>
					{/if}
				{/if}
			</div>
			<button
				onclick={() => { monitorQuery.refetch(); activityQuery.refetch(); }}
				class="rounded-md border border-border bg-surface px-3 py-1.5 text-sm text-text-secondary hover:bg-surface-hover hover:text-text-primary transition-colors"
			>
				Refresh
			</button>
		</div>
	</div>

	<div class="flex-1 overflow-auto p-6 space-y-6">
		<!-- Agent Cards Grid -->
		{#if monitorQuery.isLoading}
			<div class="text-text-muted text-sm">Loading agents...</div>
		{:else if monitorQuery.data?.agents && monitorQuery.data.agents.length > 0}
			<div class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
				{#each monitorQuery.data.agents as agent (agent.id)}
					{@const status = getStatusIndicator(agent)}
					{@const badge = getModelBadge(agent.modelName)}
					<div class="rounded-lg border border-border bg-surface p-4 space-y-3">
						<!-- Header: name, role, model, status -->
						<div class="flex items-start justify-between">
							<div class="flex items-center gap-2 min-w-0">
								<Bot class="h-4 w-4 shrink-0 text-text-muted" />
								<span class="font-medium text-text-primary truncate">{agent.agentName}</span>
								<span class="text-xs text-text-muted">({agent.role})</span>
							</div>
							<div class="flex items-center gap-2 shrink-0">
								<span class="rounded px-1.5 py-0.5 text-xs font-medium {badge.class}">{badge.label}</span>
								<div class="relative flex h-2.5 w-2.5">
									{#if status.pulse}
										<span class="absolute inline-flex h-full w-full animate-ping rounded-full {status.class} opacity-75"></span>
									{/if}
									<span class="relative inline-flex h-2.5 w-2.5 rounded-full {status.class}"></span>
								</div>
							</div>
						</div>

						<!-- Current activity -->
						<div class="text-sm text-text-secondary truncate">
							{#if agent.currentActivity}
								{agent.currentActivity}
							{:else}
								<span class="text-text-muted italic">No activity reported</span>
							{/if}
						</div>

						<!-- Current task -->
						{#if agent.currentTask}
							<div class="flex items-center gap-2 text-sm">
								<Zap class="h-3.5 w-3.5 text-accent shrink-0" />
								<a href="/board" class="text-accent hover:underline truncate">
									#{agent.currentTask.id}: {agent.currentTask.title}
								</a>
								<span class="shrink-0 rounded bg-accent/10 px-1.5 py-0.5 text-xs text-accent">
									{agent.currentTask.status}
								</span>
							</div>
						{/if}

						<!-- Stats row -->
						<div class="flex items-center gap-4 text-xs text-text-muted">
							<div class="flex items-center gap-1" title="Session duration">
								<Clock class="h-3 w-3" />
								{formatDuration(agent.sessionStarted)}
							</div>
							<div class="flex items-center gap-1" title="Tokens used">
								<Cpu class="h-3 w-3" />
								{formatTokens(agent.tokensUsed)}
							</div>
							{#if agent.lastHeartbeat}
								<div class="flex items-center gap-1" title="Last heartbeat">
									<Activity class="h-3 w-3" />
									{timeAgo(agent.lastHeartbeat)}
								</div>
							{/if}
						</div>

						<!-- Plan button -->
						{#if agent.latestPlan}
							<button
								onclick={() => viewPlan(agent)}
								disabled={loadingPlan}
								class="flex items-center gap-1.5 rounded border border-border px-2.5 py-1 text-xs text-text-secondary hover:bg-surface-hover hover:text-text-primary transition-colors"
							>
								<Eye class="h-3 w-3" />
								View Plan
								<span class="text-text-muted">({agent.latestPlan.fileName})</span>
							</button>
						{/if}
					</div>
				{/each}
			</div>
		{:else}
			<div class="rounded-lg border border-border bg-surface p-8 text-center text-text-muted">
				<Bot class="h-8 w-8 mx-auto mb-2 opacity-50" />
				<p>No agents registered. Create team members on the Team page to get started.</p>
			</div>
		{/if}

		<!-- Agent Activity -->
		<div>
			<h2 class="text-sm font-semibold text-text-primary mb-3">Agent Activity</h2>
			{#if getAgentActivity(monitorQuery.data?.agents).length > 0}
				<div class="space-y-1">
					{#each getAgentActivity(monitorQuery.data?.agents) as entry}
						<div class="flex items-start gap-3 rounded-md px-3 py-2 text-sm hover:bg-surface-hover transition-colors">
							<span class="text-text-muted text-xs w-16 shrink-0 pt-0.5">{entry.time ? timeAgo(entry.time) : '-'}</span>
							<div class="min-w-0">
								<span class="font-medium text-text-primary">{entry.agentName}</span>
								{#if entry.activity}
									<span class="text-text-muted"> &mdash; </span>
									<span class="text-text-secondary">{entry.activity}</span>
								{:else}
									<span class="text-text-muted italic"> &mdash; session started, no activity yet</span>
								{/if}
							</div>
						</div>
					{/each}
				</div>
			{:else}
				<div class="text-text-muted text-sm">No agent activity yet.</div>
			{/if}
		</div>

		<!-- Project Activity Timeline -->
		<div>
			<h2 class="text-sm font-semibold text-text-primary mb-3">Recent Project Activity</h2>
			{#if activityQuery.data && activityQuery.data.length > 0}
				<div class="space-y-1">
					{#each activityQuery.data.slice(0, 10) as item}
						<div class="flex items-center gap-3 rounded-md px-3 py-2 text-sm hover:bg-surface-hover transition-colors">
							<span class="text-text-muted text-xs w-16 shrink-0">{timeAgo(item.createdAt)}</span>
							<span class="text-text-secondary">{item.description}</span>
						</div>
					{/each}
				</div>
			{:else if activityQuery.isLoading}
				<div class="text-text-muted text-sm">Loading activity...</div>
			{:else}
				<div class="text-text-muted text-sm">No recent project activity.</div>
			{/if}
		</div>
	</div>
</div>

<!-- Plan Viewer Modal -->
{#if selectedPlan}
	<!-- svelte-ignore a11y_no_static_element_interactions -->
	<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm" onclick={() => selectedPlan = null} role="dialog">
		<!-- svelte-ignore a11y_no_static_element_interactions -->
		<div
			class="relative mx-4 max-h-[85vh] w-full max-w-4xl overflow-hidden rounded-xl border border-border bg-surface shadow-2xl"
			onclick={(e) => e.stopPropagation()}
			role="document"
		>
			<!-- Modal header -->
			<div class="flex items-center justify-between border-b border-border px-6 py-4">
				<div>
					<h3 class="font-semibold text-text-primary">{selectedPlan.agentName}'s Plan</h3>
					<p class="text-xs text-text-muted">{selectedPlan.fileName} &middot; {selectedPlan.updatedAt ? timeAgo(selectedPlan.updatedAt) : ''}</p>
				</div>
				<button onclick={() => selectedPlan = null} class="rounded-md p-1 text-text-muted hover:text-text-primary hover:bg-surface-hover transition-colors">
					<X class="h-5 w-5" />
				</button>
			</div>
			<!-- Modal body -->
			<div class="overflow-auto p-6 max-h-[70vh]">
				<pre class="whitespace-pre-wrap text-sm text-text-secondary font-mono leading-relaxed">{selectedPlan.content}</pre>
			</div>
		</div>
	</div>
{/if}
