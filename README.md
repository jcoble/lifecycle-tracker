# Lifecycle Tracker

Full-stack project management tool with AI testing system and team orchestration. Built with SvelteKit and .NET.

![Board View](https://img.shields.io/badge/Frontend-SvelteKit_5-FF3E00?logo=svelte) ![API](https://img.shields.io/badge/Backend-.NET_10-512BD4?logo=dotnet) ![DB](https://img.shields.io/badge/Database-SQLite-003B57?logo=sqlite)

## Features

- **Kanban Board** — Drag-and-drop task management with filters, search, and inline editing
- **Phases & Milestones** — Organize work into milestones with sequential phases
- **Testing System** — Test plans with steps, executions, and task completion blocking
- **Team Orchestration** — AI agent roles, spawn/shutdown, task assignment, escalations
- **Real-time Updates** — Server-Sent Events push changes to all connected clients
- **Clipboard Paste** — Paste screenshots directly onto tasks with lightbox preview
- **Activity Feed** — Track all changes with clickable navigation links
- **Keyboard Shortcuts** — `?` for help, `/` to search, `n` for new task

## Quick Start (Docker)

The fastest way to get running — no .NET or Node.js install needed:

```bash
git clone https://github.com/jcoble/lifecycle-tracker.git
cd lifecycle-tracker
cp .env.example .env
# Edit .env — set API_KEY and ORIGIN for your deployment
docker compose up
```

- **Web UI**: http://localhost (via Caddy) or http://localhost:5557 (direct)
- **API**: http://localhost/api (via Caddy) or http://localhost:5556 (direct)

Data is persisted in Docker volumes (`api-data`, `api-uploads`). To start fresh:
```bash
docker compose down -v
```

## Quick Start (Local)

If you prefer running locally:

**Prerequisites**: [.NET 10 SDK](https://dotnet.microsoft.com/download), [Node.js 20+](https://nodejs.org/) with [pnpm](https://pnpm.io/)

```bash
git clone https://github.com/jcoble/lifecycle-tracker.git
cd lifecycle-tracker
./start.sh
```

The start script restores packages, applies migrations, seeds sample data, and starts both servers.

- **Web UI**: http://localhost:5557
- **API**: http://localhost:5556

## Manual Setup

If you prefer to start services individually:

### API (.NET)

```bash
cd api
dotnet restore
dotnet run                   # Starts on port 5556, auto-migrates DB
```

### Web (SvelteKit)

```bash
cd web
pnpm install
pnpm dev                     # Starts on port 5557, proxies /api to port 5556
```

### Seed Data (Optional)

With the API running:
```bash
./seed.sh                    # Creates a default project, milestone, phases, and sample tasks
```

## Project Structure

```
lifecycle-tracker/
├── api/                     # .NET 10 Minimal API
│   ├── Api/                 # Endpoint definitions
│   │   ├── TaskEndpoints.cs
│   │   ├── TestPlanEndpoints.cs
│   │   ├── TeamEndpoints.cs
│   │   └── ...
│   ├── Data/
│   │   ├── Entities/        # EF Core entities
│   │   ├── Enums/           # Status, priority, type enums
│   │   └── LifecycleDbContext.cs
│   └── Migrations/
├── web/                     # SvelteKit frontend
│   └── src/
│       ├── lib/
│       │   ├── api/         # API client modules
│       │   ├── components/  # Svelte components
│       │   │   ├── board/   # Kanban board
│       │   │   ├── tasks/   # Task detail, clipboard
│       │   │   ├── testing/ # Test plans, execution viewer
│       │   │   └── shared/  # Badges, activity feed
│       │   ├── types/       # TypeScript interfaces
│       │   └── utils/       # Date, clipboard, classnames
│       └── routes/          # SvelteKit pages
├── mcp/                     # MCP server for Claude integration
├── start.sh                 # One-command startup
└── seed.sh                  # Initial data seeding
```

## API Overview

| Endpoint | Description |
|----------|-------------|
| `GET/POST /api/tasks` | Task CRUD with filtering |
| `POST /api/tasks/{id}/move` | Move task between columns |
| `GET/POST /api/tasks/{id}/test-plans` | Test plan management |
| `POST /api/test-plans/{id}/execute` | Start test execution |
| `GET /api/tasks/{id}/can-complete` | Check testing requirements |
| `GET/POST /api/teams` | Team member management |
| `POST /api/teams/{id}/spawn` | Spawn agent session |
| `POST /api/agents/escalations` | Agent escalation handling |
| `GET /api/events` | SSE real-time event stream |

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Frontend | SvelteKit 5, Svelte 5 (runes), TailwindCSS 4, TanStack Query |
| Backend | .NET 10 Minimal APIs, EF Core, SQLite |
| Real-time | Server-Sent Events |
| Icons | Lucide |
| Drag & Drop | svelte-dnd-action |
| MCP | Model Context Protocol SDK (optional Claude integration) |

## MCP Server (Claude Code Integration)

The `mcp/` directory contains an MCP server that exposes lifecycle operations as tools for Claude Code sessions.

### Setup

```bash
# 1. Build the MCP server
cd mcp && npm install && npm run build && cd ..

# 2. Register with Claude Code (one-time)
claude mcp add lifecycle \
  -s user \
  -e LIFECYCLE_API_URL=http://localhost:5556 \
  -e LIFECYCLE_API_KEY=your-api-key-here \
  -- node /path/to/lifecycle-tracker/mcp/build/index.js
```

For a remote deployment (e.g. shared server):
```bash
claude mcp add lifecycle \
  -s user \
  -e LIFECYCLE_API_URL=https://your-server.example.com/api \
  -e LIFECYCLE_API_KEY=your-api-key-here \
  -- node /path/to/lifecycle-tracker/mcp/build/index.js
```

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `LIFECYCLE_API_URL` | `http://localhost:5556` | API base URL |
| `LIFECYCLE_API_KEY` | *(empty)* | API key for authentication |

### Available MCP Tools

| Tool | Description |
|------|-------------|
| `get_project_context` | Load full project state: milestone, phases, tasks, activity |
| `search_tasks` | Search/filter tasks by title, status, priority, phase |
| `get_activity` | Get recent activity feed |
| `get_metrics` | Dashboard metrics: task counts, test stats, phase progress |
| `create_tasks` | Bulk-create tasks from a conversation |
| `start_task` | Move a task to InProgress |
| `complete_task` | Move a task to Done (with optional commit SHA) |
| `create_phase` | Create a new phase within a milestone |
| `breakdown_phase` | Create a phase + all its tasks in one call |
| `record_test` | Record that a test was created for a task |
| `record_test_result` | Record pass/fail result of a test run |
| `upload_screenshot` | Upload a base64 screenshot to a task |

## API Key Authentication

When `API_KEY` is set (via `.env` for Docker or `appsettings.Local.json` for local dev):

- **Browser requests** (with `Origin`/`Referer` header) bypass auth — the web UI works without a key
- **Programmatic access** (MCP, curl, scripts) requires `X-API-Key` header
- **No key set** = all requests pass through (local dev only)

Generate a key: `openssl rand -hex 16`

For local development, create `api/appsettings.Local.json`:
```json
{"ApiKey": "your-generated-key"}
```

## License

MIT
