<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { labels as labelsApi } from '$lib/api/endpoints/labels';
	import { projects as projectsApi } from '$lib/api/endpoints/projects';
	import type { Label, ProjectSettings } from '$lib/types';
	import { Plus, Trash2, Pencil, Check, XIcon, Settings, Wrench } from '@lucide/svelte';
	import { api } from '$lib/api/client';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';

	const queryClient = useQueryClient();

	const projectQuery = createQuery(() => ({
		queryKey: ['project', getCurrentProjectId()],
		queryFn: () => projectsApi.get(getCurrentProjectId()),
	}));

	const labelsQuery = createQuery(() => ({
		queryKey: ['labels', getCurrentProjectId()],
		queryFn: () => labelsApi.list(getCurrentProjectId()),
	}));

	let editing = $state(false);
	let editName = $state('');
	let editDescription = $state('');
	let editRepository = $state('');

	let newLabelName = $state('');
	let newLabelColor = $state('#3b82f6');

	// Project Settings state
	let editingSettings = $state(false);
	let settingsFields = $state<ProjectSettings>({});

	const settingsConfig: { key: keyof ProjectSettings; label: string; placeholder: string; icon: string; mono?: boolean }[] = [
		{ key: 'language', label: 'Language', placeholder: 'e.g., C#, TypeScript, Python', icon: 'code' },
		{ key: 'framework', label: 'Framework', placeholder: 'e.g., .NET 10, SvelteKit, Next.js', icon: 'box' },
		{ key: 'repositoryRoot', label: 'Repository Root', placeholder: 'e.g., /Users/me/project', icon: 'folder', mono: true },
		{ key: 'devUrl', label: 'Dev URL', placeholder: 'e.g., https://localhost:5173', icon: 'globe', mono: true },
		{ key: 'apiUrl', label: 'API URL', placeholder: 'e.g., https://localhost:5001', icon: 'globe', mono: true },
		{ key: 'unitTestCommand', label: 'Unit Test Command', placeholder: 'e.g., dotnet test --filter Category=Unit', icon: 'terminal', mono: true },
		{ key: 'integrationTestCommand', label: 'Integration Test Command', placeholder: 'e.g., dotnet test --filter Category=Integration', icon: 'terminal', mono: true },
		{ key: 'webTestTool', label: 'Web Test Tool', placeholder: 'e.g., agent-browser, playwright', icon: 'monitor' },
		{ key: 'defaultTestLevel', label: 'Default Test Level', placeholder: 'Smoke | Comprehensive | FullE2E', icon: 'shield' },
		{ key: 'testAutonomyLevel', label: 'Test Autonomy', placeholder: 'Manual | SemiAuto | AutoCreate | FullAuto', icon: 'bot' },
	];

	function parseSettings(raw?: string): ProjectSettings {
		if (!raw) return {};
		try { return JSON.parse(raw); } catch { return {}; }
	}

	function startEditingSettings() {
		settingsFields = parseSettings(projectQuery.data?.settings);
		editingSettings = true;
	}

	function cancelEditingSettings() {
		editingSettings = false;
	}

	const updateSettingsMutation = createMutation(() => ({
		mutationFn: (settings: ProjectSettings) =>
			api.put<{ id: number; settings: ProjectSettings }>(`/projects/${getCurrentProjectId()}/settings`, { settings }),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['project', getCurrentProjectId()] });
			editingSettings = false;
		},
	}));

	function saveSettings() {
		// Strip empty values
		const cleaned: Record<string, string> = {};
		for (const [k, v] of Object.entries(settingsFields)) {
			if (v && v.trim()) cleaned[k] = v.trim();
		}
		updateSettingsMutation.mutate(cleaned);
	}

	// Custom variable support
	let newVarKey = $state('');
	let newVarValue = $state('');

	function addCustomVar() {
		if (!newVarKey.trim() || !newVarValue.trim()) return;
		settingsFields = { ...settingsFields, [newVarKey.trim()]: newVarValue.trim() };
		newVarKey = '';
		newVarValue = '';
	}

	function removeCustomVar(key: string) {
		const { [key]: _, ...rest } = settingsFields;
		settingsFields = rest;
	}

	let currentSettings = $derived(parseSettings(projectQuery.data?.settings));

	const knownKeys = new Set(settingsConfig.map(c => c.key));
	function getCustomVars(s: ProjectSettings): [string, string][] {
		return Object.entries(s).filter(([k, v]) => !knownKeys.has(k) && v) as [string, string][];
	}

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
			projectsApi.update(getCurrentProjectId(), data),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['project', getCurrentProjectId()] });
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
		mutationFn: (id: number) => labelsApi.delete(id, getCurrentProjectId()),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: ['labels'] }),
	}));

	function addLabel() {
		if (!newLabelName.trim()) return;
		createLabelMutation.mutate({
			projectId: getCurrentProjectId(),
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

		<!-- Project Settings (Agent Variables) -->
		{#if projectQuery.data}
			<div class="rounded-lg border border-border bg-surface p-5">
				<div class="mb-4 flex items-center justify-between">
					<div class="flex items-center gap-2">
						<Wrench class="h-4 w-4 text-text-tertiary" />
						<h2 class="text-sm font-semibold text-text-primary">Project Settings</h2>
					</div>
					{#if !editingSettings}
						<button
							onclick={startEditingSettings}
							class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
							title="Edit settings"
						>
							<Pencil class="h-3.5 w-3.5" />
						</button>
					{/if}
				</div>

				<p class="mb-4 text-xs text-text-tertiary">
					Configure your project's tech stack. These values are used as <code class="rounded bg-bg px-1 py-0.5">{"{{variables}}"}</code> in team member prompt templates.
				</p>

				{#if editingSettings}
					<div class="space-y-3">
						{#each settingsConfig as field}
							<div>
								<label for="settings-{field.key}" class="mb-1 block text-xs text-text-tertiary">
									{field.label}
									<code class="ml-1 rounded bg-bg px-1 py-0.5 text-[10px] text-accent">{`{{${field.key}}}`}</code>
								</label>
								<input
									id="settings-{field.key}"
									type="text"
									value={settingsFields[field.key] || ''}
									oninput={(e) => { settingsFields = { ...settingsFields, [field.key]: e.currentTarget.value }; }}
									placeholder={field.placeholder}
									class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none {field.mono ? 'font-mono' : ''}"
								/>
							</div>
						{/each}

						<!-- Custom variables -->
						{#if getCustomVars(settingsFields).length > 0}
							<div class="border-t border-border pt-3">
								<p class="mb-2 text-xs font-medium text-text-tertiary">Custom Variables</p>
								{#each getCustomVars(settingsFields) as [key, value]}
									<div class="mb-2 flex items-center gap-2">
										<code class="min-w-[100px] rounded bg-bg px-2 py-1 text-xs text-accent">{`{{${key}}}`}</code>
										<span class="flex-1 truncate text-sm text-text-primary">{value}</span>
										<button
											onclick={() => removeCustomVar(key)}
											class="rounded p-1 text-text-tertiary hover:bg-danger/10 hover:text-danger"
										>
											<Trash2 class="h-3 w-3" />
										</button>
									</div>
								{/each}
							</div>
						{/if}

						<div class="border-t border-border pt-3">
							<p class="mb-2 text-xs font-medium text-text-tertiary">Add Custom Variable</p>
							<div class="flex items-center gap-2">
								<input
									type="text"
									bind:value={newVarKey}
									placeholder="variableName"
									class="w-32 rounded-md border border-border bg-bg px-2 py-1.5 text-xs font-mono text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none"
								/>
								<input
									type="text"
									bind:value={newVarValue}
									placeholder="value"
									onkeydown={(e) => { if (e.key === 'Enter') addCustomVar(); }}
									class="flex-1 rounded-md border border-border bg-bg px-2 py-1.5 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none"
								/>
								<button
									onclick={addCustomVar}
									disabled={!newVarKey.trim() || !newVarValue.trim()}
									class="rounded-md bg-surface-hover px-2 py-1.5 text-xs text-text-secondary hover:text-text-primary disabled:opacity-50"
								>
									<Plus class="h-3.5 w-3.5" />
								</button>
							</div>
						</div>

						<div class="flex items-center gap-2 pt-1">
							<button
								onclick={saveSettings}
								disabled={updateSettingsMutation.isPending}
								class="flex items-center gap-1 rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
							>
								<Check class="h-3.5 w-3.5" />
								Save Settings
							</button>
							<button
								onclick={cancelEditingSettings}
								class="flex items-center gap-1 rounded-md border border-border px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
							>
								<XIcon class="h-3.5 w-3.5" />
								Cancel
							</button>
						</div>
					</div>
				{:else}
					{#if Object.values(currentSettings).some(v => v)}
						<div class="space-y-2">
							{#each settingsConfig as field}
								{#if currentSettings[field.key]}
									<div class="flex items-start gap-3 rounded-md border border-border bg-bg px-3 py-2">
										<div class="min-w-[140px]">
											<p class="text-xs text-text-tertiary">{field.label}</p>
										</div>
										<p class="text-sm {field.mono ? 'font-mono' : ''} text-text-primary">{currentSettings[field.key]}</p>
									</div>
								{/if}
							{/each}
							{#each getCustomVars(currentSettings) as [key, value]}
								<div class="flex items-start gap-3 rounded-md border border-border bg-bg px-3 py-2">
									<div class="min-w-[140px]">
										<code class="rounded bg-surface px-1.5 py-0.5 text-xs text-accent">{`{{${key}}}`}</code>
									</div>
									<p class="text-sm text-text-primary">{value}</p>
								</div>
							{/each}
						</div>
					{:else}
						<p class="text-sm text-text-tertiary">No settings configured yet. Click edit to set up your project's tech stack.</p>
					{/if}
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
