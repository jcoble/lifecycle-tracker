<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { team as teamApi, escalations as escalationsApi } from '$lib/api/endpoints/team';
	import type { TeamMember, AgentEscalation } from '$lib/types';
	import { formatRelative } from '$lib/utils/date';
	import {
		Plus,
		Trash2,
		Play,
		Square,
		AlertTriangle,
		CheckCircle,
		User,
		Bot,
		Shield,
		Search,
		Code,
		Eye
	} from '@lucide/svelte';

	const queryClient = useQueryClient();

	const teamQuery = createQuery(() => ({
		queryKey: ['team', 1],
		queryFn: () => teamApi.list(1),
	}));

	const escalationsQuery = createQuery(() => ({
		queryKey: ['escalations', 1],
		queryFn: () => escalationsApi.list(1),
	}));

	let showCreateForm = $state(false);
	let newRole = $state('Developer');
	let newName = $state('');
	let newModel = $state('claude-sonnet-4-5');
	let newPersistent = $state(false);
	let resolveText = $state('');

	const roles = ['ProjectManager', 'Developer', 'QA', 'Reviewer', 'Researcher', 'DevOps'];
	const models = ['claude-opus-4-6', 'claude-sonnet-4-5', 'claude-haiku-4-5'];

	const roleIcons: Record<string, typeof User> = {
		ProjectManager: Shield,
		Developer: Code,
		QA: Search,
		Reviewer: Eye,
		Researcher: Search,
		DevOps: Code,
	};

	const statusColors: Record<string, string> = {
		Active: 'text-success bg-success/10',
		Idle: 'text-text-tertiary bg-text-tertiary/10',
		Suspended: 'text-warning bg-warning/10',
	};

	const createMemberMutation = createMutation(() => ({
		mutationFn: (data: { projectId: number; role: string; agentName: string; modelName: string; isPersistent: boolean }) =>
			teamApi.create(data),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['team'] });
			showCreateForm = false;
			newName = '';
		},
	}));

	const deleteMemberMutation = createMutation(() => ({
		mutationFn: (id: number) => teamApi.delete(id),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['team'] }),
	}));

	const spawnMutation = createMutation(() => ({
		mutationFn: (id: number) => teamApi.spawn(id),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['team'] }),
	}));

	const shutdownMutation = createMutation(() => ({
		mutationFn: (id: number) => teamApi.shutdown(id),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['team'] }),
	}));

	const resolveEscalationMutation = createMutation(() => ({
		mutationFn: ({ id, resolution }: { id: number; resolution: string }) =>
			escalationsApi.resolve(id, { resolution }),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['escalations'] }),
	}));

	function createMember() {
		if (!newName.trim()) return;
		createMemberMutation.mutate({
			projectId: 1,
			role: newRole,
			agentName: newName.trim(),
			modelName: newModel,
			isPersistent: newPersistent,
		});
	}

	let pendingEscalations = $derived(
		(escalationsQuery.data || []).filter((e: AgentEscalation) => e.status === 'Pending')
	);
</script>

<svelte:head>
	<title>Team - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-6 flex items-center justify-between">
		<div>
			<h1 class="text-2xl font-bold text-text-primary">Team</h1>
			<p class="mt-1 text-sm text-text-tertiary">Manage AI agent team members</p>
		</div>
		<button
			onclick={() => (showCreateForm = !showCreateForm)}
			class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover"
		>
			<Plus class="h-3.5 w-3.5" />
			Add Member
		</button>
	</div>

	<!-- Escalations Banner -->
	{#if pendingEscalations.length > 0}
		<div class="mb-6 rounded-lg border border-warning/30 bg-warning/5 p-4">
			<h2 class="mb-2 flex items-center gap-2 text-sm font-semibold text-warning">
				<AlertTriangle class="h-4 w-4" />
				Pending Escalations ({pendingEscalations.length})
			</h2>
			<div class="space-y-2">
				{#each pendingEscalations as escalation}
					<div class="flex items-start justify-between rounded-md border border-border bg-surface p-3">
						<div class="min-w-0 flex-1">
							<p class="text-sm text-text-primary">{escalation.description}</p>
							<p class="mt-0.5 text-xs text-text-tertiary">
								{escalation.taskTitle ? `Task: ${escalation.taskTitle}` : ''} - {formatRelative(escalation.createdAt)}
							</p>
						</div>
						<div class="ml-3 flex items-center gap-2">
							<input
								type="text"
								bind:value={resolveText}
								placeholder="Resolution..."
								class="rounded border border-border bg-bg px-2 py-1 text-xs text-text-primary focus:border-accent focus:outline-none"
							/>
							<button
								onclick={() => {
									resolveEscalationMutation.mutate({ id: escalation.id, resolution: resolveText });
									resolveText = '';
								}}
								class="rounded bg-success px-2 py-1 text-xs text-white hover:bg-success/80"
							>
								Resolve
							</button>
						</div>
					</div>
				{/each}
			</div>
		</div>
	{/if}

	<!-- Create Form -->
	{#if showCreateForm}
		<div class="mb-6 rounded-lg border border-border bg-surface p-5">
			<h2 class="mb-3 text-sm font-semibold text-text-primary">New Team Member</h2>
			<div class="grid grid-cols-2 gap-3">
				<div>
					<label class="mb-1 block text-xs text-text-tertiary">Agent Name</label>
					<input
						type="text"
						bind:value={newName}
						class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
						placeholder="e.g., dev-backend"
					/>
				</div>
				<div>
					<label class="mb-1 block text-xs text-text-tertiary">Role</label>
					<select
						bind:value={newRole}
						class="w-full rounded-md border border-border bg-bg px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					>
						{#each roles as role}
							<option value={role}>{role}</option>
						{/each}
					</select>
				</div>
				<div>
					<label class="mb-1 block text-xs text-text-tertiary">Model</label>
					<select
						bind:value={newModel}
						class="w-full rounded-md border border-border bg-bg px-2 py-1.5 text-sm font-mono text-text-primary focus:border-accent focus:outline-none"
					>
						{#each models as model}
							<option value={model}>{model}</option>
						{/each}
					</select>
				</div>
				<div class="flex items-end">
					<label class="flex items-center gap-2 text-sm text-text-secondary">
						<input type="checkbox" bind:checked={newPersistent} class="rounded" />
						Persistent
					</label>
				</div>
			</div>
			<div class="mt-3 flex items-center gap-2">
				<button
					onclick={createMember}
					disabled={!newName.trim()}
					class="rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
				>
					Create
				</button>
				<button
					onclick={() => (showCreateForm = false)}
					class="rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
				>
					Cancel
				</button>
			</div>
		</div>
	{/if}

	<!-- Team Grid -->
	<div class="max-w-4xl">
		{#if teamQuery.isLoading}
			<p class="text-text-tertiary">Loading team...</p>
		{:else if teamQuery.data && teamQuery.data.length > 0}
			<div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
				{#each teamQuery.data as member}
					{@const RoleIcon = roleIcons[member.role] || Bot}
					<div class="rounded-lg border border-border bg-surface p-4">
						<!-- Header -->
						<div class="mb-3 flex items-start justify-between">
							<div class="flex items-center gap-2">
								<div class="rounded-md bg-accent/10 p-1.5">
									<RoleIcon class="h-4 w-4 text-accent" />
								</div>
								<div>
									<p class="text-sm font-medium text-text-primary">{member.agentName}</p>
									<p class="text-[10px] text-text-tertiary">{member.role}</p>
								</div>
							</div>
							<span class="rounded-full px-2 py-0.5 text-[10px] font-medium {statusColors[member.status] || 'text-text-tertiary'}">
								{member.status}
							</span>
						</div>

						<!-- Info -->
						<div class="mb-3 space-y-1 text-xs text-text-tertiary">
							<p class="font-mono">{member.modelName}</p>
							{#if member.isPersistent}
								<p class="text-accent">Persistent</p>
							{/if}
							{#if member.currentTask}
								<p class="text-text-secondary">Working on Task #{member.currentTask.taskId}</p>
							{/if}
							{#if member.lastActiveAt}
								<p>Last active {formatRelative(member.lastActiveAt)}</p>
							{/if}
						</div>

						<!-- Actions -->
						<div class="flex items-center gap-1 border-t border-border pt-2">
							{#if member.status === 'Active'}
								<button
									onclick={() => shutdownMutation.mutate(member.id)}
									class="flex items-center gap-1 rounded px-2 py-1 text-xs text-text-secondary transition-colors hover:bg-surface-hover"
									title="Shutdown"
								>
									<Square class="h-3 w-3" />
									Stop
								</button>
							{:else}
								<button
									onclick={() => spawnMutation.mutate(member.id)}
									class="flex items-center gap-1 rounded px-2 py-1 text-xs text-accent transition-colors hover:bg-accent/10"
									title="Spawn"
								>
									<Play class="h-3 w-3" />
									Spawn
								</button>
							{/if}
							<button
								onclick={() => {
									if (confirm(`Delete ${member.agentName}?`)) deleteMemberMutation.mutate(member.id);
								}}
								class="ml-auto rounded p-1 text-text-tertiary transition-colors hover:text-danger"
							>
								<Trash2 class="h-3 w-3" />
							</button>
						</div>
					</div>
				{/each}
			</div>
		{:else}
			<div class="rounded-lg border-2 border-dashed border-border p-8 text-center">
				<Bot class="mx-auto mb-2 h-8 w-8 text-text-tertiary" />
				<p class="text-sm text-text-tertiary">No team members yet</p>
				<p class="mt-1 text-xs text-text-tertiary">Add AI agents to automate your development workflow</p>
			</div>
		{/if}
	</div>
</div>
