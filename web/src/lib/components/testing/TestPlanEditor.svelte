<script lang="ts">
	import type { TestLevel, TestStepType } from '$lib/types';
	import { testPlans as testPlansApi } from '$lib/api/endpoints/testPlans';
	import { Plus, Trash2, GripVertical } from '@lucide/svelte';

	let {
		taskId,
		onsave,
		oncancel,
	}: {
		taskId: number;
		onsave?: () => void;
		oncancel?: () => void;
	} = $props();

	let name = $state('');
	let description = $state('');
	let requiredLevel = $state<TestLevel>('Functional');
	let steps = $state<Array<{
		stepType: TestStepType;
		description: string;
		expectedResult: string;
		automationCommand: string;
		requiresManualVerification: boolean;
	}>>([]);
	let saving = $state(false);

	function addStep() {
		steps = [...steps, {
			stepType: 'Action',
			description: '',
			expectedResult: '',
			automationCommand: '',
			requiresManualVerification: false,
		}];
	}

	function removeStep(index: number) {
		steps = steps.filter((_, i) => i !== index);
	}

	async function save() {
		if (!name.trim() || steps.length === 0) return;
		saving = true;
		try {
			await testPlansApi.create(taskId, {
				name: name.trim(),
				description: description.trim() || undefined,
				requiredLevel,
				steps: steps.map((s) => ({
					stepType: s.stepType,
					description: s.description,
					expectedResult: s.expectedResult || undefined,
					automationCommand: s.automationCommand || undefined,
					requiresManualVerification: s.requiresManualVerification,
				})),
			});
			onsave?.();
		} finally {
			saving = false;
		}
	}

	const stepTypes: TestStepType[] = ['Setup', 'Action', 'Assertion', 'Teardown'];
	const levels: TestLevel[] = ['Smoke', 'Functional', 'Comprehensive', 'FullE2E'];
</script>

<div class="rounded-lg border border-border bg-surface p-4">
	<h3 class="mb-3 text-sm font-semibold text-text-primary">New Test Plan</h3>

	<div class="space-y-3">
		<div class="grid grid-cols-2 gap-3">
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Plan Name</label>
				<input
					type="text"
					bind:value={name}
					class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
					placeholder="e.g., Login Flow Tests"
				/>
			</div>
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Test Level</label>
				<select
					bind:value={requiredLevel}
					class="w-full rounded-md border border-border bg-bg px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				>
					{#each levels as level}
						<option value={level}>{level}</option>
					{/each}
				</select>
			</div>
		</div>

		<div>
			<label class="mb-1 block text-xs text-text-tertiary">Description</label>
			<textarea
				bind:value={description}
				rows={2}
				class="w-full rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none resize-none"
				placeholder="What does this test plan cover?"
			></textarea>
		</div>

		<!-- Steps -->
		<div>
			<div class="mb-2 flex items-center justify-between">
				<label class="text-xs text-text-tertiary">Steps ({steps.length})</label>
				<button
					onclick={addStep}
					class="flex items-center gap-1 text-xs text-accent hover:text-accent-hover"
				>
					<Plus class="h-3 w-3" />
					Add Step
				</button>
			</div>

			<div class="space-y-2">
				{#each steps as step, i}
					<div class="rounded-md border border-border bg-bg p-2.5">
						<div class="mb-2 flex items-center gap-2">
							<GripVertical class="h-3.5 w-3.5 text-text-tertiary cursor-grab" />
							<span class="text-xs font-medium text-text-tertiary">#{i + 1}</span>
							<select
								bind:value={step.stepType}
								class="rounded border border-border bg-surface px-1.5 py-0.5 text-xs text-text-primary focus:border-accent focus:outline-none"
							>
								{#each stepTypes as st}
									<option value={st}>{st}</option>
								{/each}
							</select>
							<label class="ml-auto flex items-center gap-1 text-[10px] text-text-tertiary">
								<input type="checkbox" bind:checked={step.requiresManualVerification} class="rounded" />
								Manual
							</label>
							<button
								onclick={() => removeStep(i)}
								class="rounded p-0.5 text-text-tertiary hover:text-danger"
							>
								<Trash2 class="h-3 w-3" />
							</button>
						</div>
						<input
							type="text"
							bind:value={step.description}
							class="mb-1.5 w-full rounded border border-border bg-surface px-2 py-1 text-xs text-text-primary focus:border-accent focus:outline-none"
							placeholder="Step description..."
						/>
						<div class="grid grid-cols-2 gap-2">
							<input
								type="text"
								bind:value={step.expectedResult}
								class="rounded border border-border bg-surface px-2 py-1 text-xs text-text-primary focus:border-accent focus:outline-none"
								placeholder="Expected result..."
							/>
							<input
								type="text"
								bind:value={step.automationCommand}
								class="rounded border border-border bg-surface px-2 py-1 text-xs font-mono text-text-primary focus:border-accent focus:outline-none"
								placeholder="agent-browser command..."
							/>
						</div>
					</div>
				{/each}
			</div>

			{#if steps.length === 0}
				<button
					onclick={addStep}
					class="w-full rounded-md border-2 border-dashed border-border p-3 text-xs text-text-tertiary hover:border-accent hover:text-accent"
				>
					Add your first test step
				</button>
			{/if}
		</div>
	</div>

	<div class="mt-4 flex items-center gap-2">
		<button
			onclick={save}
			disabled={!name.trim() || steps.length === 0 || saving}
			class="rounded-md bg-accent px-4 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
		>
			{saving ? 'Creating...' : 'Create Plan'}
		</button>
		{#if oncancel}
			<button
				onclick={oncancel}
				class="rounded-md border border-border px-4 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover"
			>
				Cancel
			</button>
		{/if}
	</div>
</div>
