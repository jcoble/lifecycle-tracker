<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { labels as labelsApi } from '$lib/api/endpoints/labels';
	import { projects as projectsApi } from '$lib/api/endpoints/projects';
	import type { Label } from '$lib/types';
	import { Plus, Trash2, Pencil, Check, XIcon } from '@lucide/svelte';

	const queryClient = useQueryClient();

	const projectQuery = createQuery(() => ({
		queryKey: ['project', 1],
		queryFn: () => projectsApi.get(1),
	}));

	const labelsQuery = createQuery(() => ({
		queryKey: ['labels'],
		queryFn: () => labelsApi.list(1),
	}));

	let editing = $state(false);
	let editName = $state('');
	let editDescription = $state('');
	let editRepository = $state('');

	let newLabelName = $state('');
	let newLabelColor = $state('#3b82f6');

	function startEditing() {
		if (!projectQuery.data) return;
		editName = projectQuery.data.name;
		editDescription = projectQuery.data.description || '';
		editRepository = projectQuery.data.repository || '';
		editing = true;
	}

	function cancelEditing() {
		editing = false;
	}

	const updateProjectMutation = createMutation(() => ({
		mutationFn: (data: { name: string; description?: string; repository?: string }) =>
			projectsApi.update(1, data),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['project', 1] });
			queryClient.invalidateQueries({ queryKey: ['dashboard'] });
			editing = false;
		},
	}));

	function saveProject() {
		if (!editName.trim()) return;
		updateProjectMutation.mutate({
			name: editName.trim(),
			description: editDescription.trim() || undefined,
			repository: editRepository.trim() || undefined,
		});
	}

	const createLabelMutation = createMutation(() => ({
		mutationFn: (data: Partial<Label>) => labelsApi.create(data),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['labels'] });
			newLabelName = '';
			newLabelColor = '#3b82f6';
		},
	}));

	const deleteLabelMutation = createMutation(() => ({
		mutationFn: (id: number) => labelsApi.delete(id, 1),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['labels'] }),
	}));

	function addLabel() {
		if (!newLabelName.trim()) return;
		createLabelMutation.mutate({
			projectId: 1,
			name: newLabelName.trim(),
			color: newLabelColor,
		});
	}
</script>

<svelte:head>
	<title>Settings - Lifecycle Tracker</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-6">
		<h1 class="text-2xl font-bold text-text-primary">Settings</h1>
	</div>

	<div class="max-w-2xl space-y-8">
		<!-- Project Info -->
		{#if projectQuery.data}
			<div class="rounded-lg border border-border bg-surface p-5">
				<div class="mb-4 flex items-center justify-between">
					<h2 class="text-sm font-semibold text-text-primary">Project</h2>
					{#if !editing}
						<button
							onclick={startEditing}
							class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
							title="Edit project"
						>
							<Pencil class="h-3.5 w-3.5" />
						</button>
					{/if}
				</div>

				{#if editing}
					<div class="space-y-3">
						<div>
							<label for="project-name" class="mb-1 block text-xs text-text-tertiary">Name</label>
							<input
								id="project-name"
								type="text"
								bind:value={editName}
								class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
							/>
						</div>
						<div>
							<label for="project-desc" class="mb-1 block text-xs text-text-tertiary">Description</label>
							<textarea
								id="project-desc"
								bind:value={editDescription}
								rows={3}
								class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none resize-none"
								placeholder="Project description..."
							></textarea>
						</div>
						<div>
							<label for="project-repo" class="mb-1 block text-xs text-text-tertiary">Repository</label>
							<input
								id="project-repo"
								type="text"
								bind:value={editRepository}
								class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm font-mono text-text-primary focus:border-accent focus:outline-none"
								placeholder="https://github.com/..."
							/>
						</div>
						<div class="flex items-center gap-2 pt-1">
							<button
								onclick={saveProject}
								disabled={!editName.trim() || updateProjectMutation.isPending}
								class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
							>
								<Check class="h-3.5 w-3.5" />
								Save
							</button>
							<button
								onclick={cancelEditing}
								class="flex items-center gap-1 rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
							>
								<XIcon class="h-3.5 w-3.5" />
								Cancel
							</button>
						</div>
					</div>
				{:else}
					<div class="space-y-3">
						<div>
							<p class="mb-1 text-xs text-text-tertiary">Name</p>
							<p class="text-sm text-text-primary">{projectQuery.data.name}</p>
						</div>
						{#if projectQuery.data.description}
							<div>
								<p class="mb-1 text-xs text-text-tertiary">Description</p>
								<p class="text-sm text-text-secondary">{projectQuery.data.description}</p>
							</div>
						{/if}
						{#if projectQuery.data.repository}
							<div>
								<p class="mb-1 text-xs text-text-tertiary">Repository</p>
								<p class="font-mono text-sm text-text-secondary">{projectQuery.data.repository}</p>
							</div>
						{/if}
					</div>
				{/if}
			</div>
		{/if}

		<!-- Labels -->
		<div class="rounded-lg border border-border bg-surface p-5">
			<h2 class="mb-4 text-sm font-semibold text-text-primary">Labels</h2>

			{#if labelsQuery.data && labelsQuery.data.length > 0}
				<div class="mb-4 space-y-2">
					{#each labelsQuery.data as label}
						<div class="flex items-center justify-between rounded-md border border-border bg-bg px-3 py-2">
							<div class="flex items-center gap-2">
								<span
									class="h-3 w-3 rounded-full"
									style="background: {label.color};"
								></span>
								<span class="text-sm text-text-primary">{label.name}</span>
								{#if label.description}
									<span class="text-xs text-text-tertiary">- {label.description}</span>
								{/if}
							</div>
							<button
								onclick={() => deleteLabelMutation.mutate(label.id)}
								class="rounded p-1 text-text-tertiary transition-colors hover:bg-danger/10 hover:text-danger"
							>
								<Trash2 class="h-3.5 w-3.5" />
							</button>
						</div>
					{/each}
				</div>
			{:else}
				<p class="mb-4 text-sm text-text-tertiary">No labels created yet</p>
			{/if}

			<!-- Add label -->
			<div class="flex items-center gap-2">
				<input
					type="color"
					bind:value={newLabelColor}
					class="h-8 w-8 cursor-pointer rounded border border-border bg-transparent"
				/>
				<input
					type="text"
					bind:value={newLabelName}
					onkeydown={(e) => { if (e.key === 'Enter') addLabel(); }}
					placeholder="Label name..."
					class="flex-1 rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none"
				/>
				<button
					onclick={addLabel}
					disabled={!newLabelName.trim()}
					class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
				>
					<Plus class="h-3.5 w-3.5" />
					Add
				</button>
			</div>
		</div>

		<!-- API Info -->
		<div class="rounded-lg border border-border bg-surface p-5">
			<h2 class="mb-4 text-sm font-semibold text-text-primary">API</h2>
			<div>
				<p class="mb-1 text-xs text-text-tertiary">Base URL</p>
				<p class="font-mono text-sm text-text-secondary">http://localhost:5556/api</p>
			</div>
			<div class="mt-3">
				<p class="mb-1 text-xs text-text-tertiary">Documentation</p>
				<p class="text-sm text-text-secondary">API key is required via X-API-Key header</p>
			</div>
		</div>
	</div>
</div>
