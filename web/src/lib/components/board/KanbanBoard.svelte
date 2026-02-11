<script lang="ts">
	import type { Task, TaskStatus, Phase, Label, Milestone } from '$lib/types';
	import BoardColumn from './BoardColumn.svelte';
	import FilterBar from './FilterBar.svelte';
	import TaskDetail from '$lib/components/tasks/TaskDetail.svelte';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import { getCurrentProjectId } from '$lib/stores/project.svelte';

	let {
		tasks,
		phases = [] as Phase[],
		milestones = [] as Milestone[],
		labels = [] as Label[],
		onTaskUpdated,
	}: {
		tasks: Task[];
		phases?: Phase[];
		milestones?: Milestone[];
		labels?: Label[];
		onTaskUpdated?: () => void;
	} = $props();

	let selectedTask = $state<Task | null>(null);
	let filters = $state<Record<string, string>>({});
	let boardScrollEl = $state<HTMLDivElement | null>(null);
	let archiveNotice = $state<string | null>(null);
	let moveError = $state<string | null>(null);

	const columns: { status: TaskStatus; title: string }[] = [
		{ status: 'Backlog', title: 'Backlog' },
		{ status: 'Todo', title: 'Todo' },
		{ status: 'InProgress', title: 'In Progress' },
		{ status: 'Review', title: 'Review' },
		{ status: 'Blocked', title: 'Blocked' },
		{ status: 'Done', title: 'Done' },
		{ status: 'Cancelled', title: 'Cancelled' },
	];

	// Phases filtered by selected milestone
	let filteredPhases = $derived.by(() => {
		if (!filters.milestone) return phases;
		const msId = Number(filters.milestone);
		return phases.filter(p => p.milestoneId === msId);
	});

	let filteredTasks = $derived.by(() => {
		let result = tasks;
		if (filters.search) {
			const s = filters.search.trim().toLowerCase();
			// Support #123 or plain number search by task ID
			const idMatch = s.match(/^#?(\d+)$/);
			if (idMatch) {
				const searchId = Number(idMatch[1]);
				result = result.filter((t) => t.id === searchId);
			} else {
				result = result.filter(
					(t) => t.title.toLowerCase().includes(s) || t.description?.toLowerCase().includes(s)
				);
			}
		}
		if (filters.milestone) {
			const milestonePhaseIds = new Set(filteredPhases.map(p => p.id));
			result = result.filter((t) => t.phaseId && milestonePhaseIds.has(t.phaseId));
		}
		if (filters.phase) result = result.filter((t) => t.phaseId === Number(filters.phase));
		if (filters.priority) result = result.filter((t) => t.priority === filters.priority);
		if (filters.type) result = result.filter((t) => t.type === filters.type);
		if (filters.source) result = result.filter((t) => t.source === filters.source);
		if (filters.label)
			result = result.filter((t) => t.labels?.some((l) => l.id === Number(filters.label)));
		return result;
	});

	// Group tasks by status into column data
	let columnData = $state<Record<TaskStatus, Task[]>>({
		Backlog: [],
		Todo: [],
		InProgress: [],
		Review: [],
		Blocked: [],
		Done: [],
		Cancelled: [],
	});

	// Update column data when filtered tasks change
	$effect(() => {
		const grouped: Record<string, Task[]> = {};
		for (const col of columns) {
			grouped[col.status] = [];
		}
		for (const task of filteredTasks) {
			if (grouped[task.status]) {
				grouped[task.status].push(task);
			}
		}

		const sortBy = filters.sortBy || 'board';
		const sortDir = filters.sortDir === 'desc' ? -1 : 1;
		const priorityOrder: Record<string, number> = { P1: 1, P2: 2, P3: 3, P4: 4 };

		const compareText = (a: string | undefined, b: string | undefined) =>
			(a || '').localeCompare(b || '', undefined, { sensitivity: 'base' });

		const compareTask = (a: Task, b: Task) => {
			switch (sortBy) {
				case 'id':
					return (a.id - b.id) * sortDir;
				case 'title':
					return compareText(a.title, b.title) * sortDir;
				case 'description':
					return compareText(a.description, b.description) * sortDir;
				case 'priority':
					return ((priorityOrder[a.priority] ?? 99) - (priorityOrder[b.priority] ?? 99)) * sortDir;
				case 'createdAt':
					return ((new Date(a.createdAt).getTime() || 0) - (new Date(b.createdAt).getTime() || 0)) * sortDir;
				case 'updatedAt':
					return ((new Date(a.updatedAt).getTime() || 0) - (new Date(b.updatedAt).getTime() || 0)) * sortDir;
				default:
					return (a.orderInColumn - b.orderInColumn) * sortDir;
			}
		};

		// Sort each column according to selected sort mode
		for (const key of Object.keys(grouped)) {
			grouped[key].sort(compareTask);
		}
		columnData = grouped as Record<TaskStatus, Task[]>;
	});

	function handleConsider(status: TaskStatus, e: CustomEvent) {
		columnData[status] = e.detail.items;
		columnData = columnData; // trigger reactivity
	}

	async function handleFinalize(status: TaskStatus, e: CustomEvent) {
		columnData[status] = e.detail.items;
		columnData = columnData;

		// Find the task that was dropped and update it
		const droppedInfo = e.detail.info;
		if (droppedInfo?.id) {
			const droppedId = droppedInfo.id;
			const orderInColumn = columnData[status].findIndex((t) => t.id === droppedId);
			if (orderInColumn >= 0) {
				try {
					await tasksApi.move(droppedId, status, orderInColumn);
					onTaskUpdated?.();
				} catch (err: unknown) {
					// Show validation error from transition rules
					if (err instanceof Error && err.message) {
						moveError = err.message;
						setTimeout(() => { moveError = null; }, 4000);
					}
					// Refetch to revert the drag
					onTaskUpdated?.();
				}
			}
		}
	}

	async function handleQuickAdd(status: TaskStatus, title: string) {
		try {
			await tasksApi.create({
				title,
				status,
				projectId: getCurrentProjectId(),
				priority: 'P3',
				type: 'Feature',
				source: 'Manual',
				orderInColumn: columnData[status].length,
			});
			onTaskUpdated?.();
		} catch {
			// Handle error
		}
	}

	function handleCardClick(task: Task) {
		selectedTask = task;
	}

	function handleTaskUpdate(updated: Task) {
		selectedTask = updated;
		onTaskUpdated?.();
	}

	async function handleArchiveCompleted() {
		const result = await tasksApi.archiveCompleted({
			projectId: getCurrentProjectId(),
			completedPhasesOnly: true,
		});

		const archivedCount = result.archivedCount ?? 0;
		archiveNotice = archivedCount > 0
			? `Archived ${archivedCount} completed task${archivedCount === 1 ? '' : 's'}.`
			: 'No completed tasks to archive.';

		setTimeout(() => {
			archiveNotice = null;
		}, 2500);

		onTaskUpdated?.();
	}

	function handleBoardWheel(e: WheelEvent) {
		if (!boardScrollEl) return;
		if (Math.abs(e.deltaX) > 0) return; // Keep native horizontal gestures intact

		const target = e.target as HTMLElement | null;
		const columnScroller = target?.closest<HTMLElement>('[data-column-scroll="true"]');

		// Allow normal vertical column scrolling while content can still scroll vertically.
		if (!e.shiftKey && columnScroller) {
			const scrollingDown = e.deltaY > 0;
			const canScrollUp = columnScroller.scrollTop > 0;
			const canScrollDown =
				columnScroller.scrollTop + columnScroller.clientHeight < columnScroller.scrollHeight - 1;

			if ((scrollingDown && canScrollDown) || (!scrollingDown && canScrollUp)) {
				return;
			}
		}

		if (e.deltaY !== 0) {
			boardScrollEl.scrollLeft += e.deltaY;
			e.preventDefault();
		}
	}
</script>

<div class="flex h-full flex-col">
	<FilterBar
		phases={filteredPhases}
		{milestones}
		{labels}
		onchange={(f) => (filters = f)}
		onArchiveCompleted={handleArchiveCompleted}
	/>

	{#if archiveNotice}
		<div class="border-b border-border px-4 py-2 text-xs text-text-secondary">
			{archiveNotice}
		</div>
	{/if}

	{#if moveError}
		<div class="border-b border-danger/30 bg-danger/10 px-4 py-2 text-xs text-danger">
			{moveError}
		</div>
	{/if}

	<!-- Board -->
	<div
		class="flex flex-1 gap-3 sm:gap-4 overflow-x-auto overflow-y-hidden p-2 sm:p-4 -webkit-overflow-scrolling-touch"
		bind:this={boardScrollEl}
		onwheel={handleBoardWheel}
	>
		{#each columns as col}
			<BoardColumn
				status={col.status}
				title={col.title}
				tasks={columnData[col.status] || []}
				dragEnabled={(filters.sortBy || 'board') === 'board'}
				onCardClick={handleCardClick}
				onDndConsider={handleConsider}
				onDndFinalize={handleFinalize}
				onQuickAdd={handleQuickAdd}
			/>
		{/each}
	</div>
</div>

<!-- Task detail slide-in -->
{#if selectedTask}
	<TaskDetail
		task={selectedTask}
		{phases}
		onclose={() => (selectedTask = null)}
		onupdate={handleTaskUpdate}
		ondelete={() => { selectedTask = null; onTaskUpdated?.(); }}
	/>
{/if}
