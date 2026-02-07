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

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) with [pnpm](https://pnpm.io/)

## Quick Start

```bash
# Clone
git clone https://github.com/jcoble/lifecycle-tracker.git
cd lifecycle-tracker

# Start everything (API + Web + seed data)
./start.sh
```

The start script handles everything: restores packages, applies migrations, seeds initial data, and starts both servers.

- **Web UI**: http://localhost:5557
- **API**: http://localhost:5556

## Manual Setup

If you prefer to start services individually:

### API (.NET)

```bash
cd api
dotnet restore
dotnet ef database update    # Apply migrations (requires dotnet-ef tool)
dotnet run                   # Starts on port 5556
```

To install the EF tool if needed:
```bash
dotnet tool install --global dotnet-ef
```

### Web (SvelteKit)

```bash
cd web
pnpm install
pnpm dev                     # Starts on port 5557, proxies /api to port 5556
```

### Seed Data

With the API running:
```bash
./seed.sh                    # Creates a default project and milestone
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

## MCP Server (Optional)

The `mcp/` directory contains an MCP server that exposes lifecycle operations as tools for Claude:

```bash
cd mcp
npm install
npm run build
npm start
```

## License

MIT
