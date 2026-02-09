# Lifecycle Tracker — System Architecture

```mermaid
graph TB
    subgraph Claude["Claude Code Session"]
        MCP[MCP Server<br/>Node.js stdio<br/>21+ tools]
        HOOK[PreToolUse Hook<br/>check-can-complete.js]
    end

    subgraph API[".NET 10 Minimal API (:5556)"]
        MW[ApiKeyAuthMiddleware]
        EP[Endpoints<br/>Project, Milestone, Phase,<br/>Task, TestPlan, Team,<br/>Activity, SSE]
        EF[EF Core]
    end

    subgraph Web["SvelteKit Web (:5557)"]
        KB[Kanban Board]
        TD[Task Detail + Tests]
        TM[Team Management]
        MT[Metrics Dashboard]
    end

    DB[(SQLite<br/>lifecycle.db)]

    MCP -->|JSON-RPC stdio| API
    HOOK -->|HTTP check| API
    MCP -.->|env: LIFECYCLE_API_URL<br/>LIFECYCLE_API_KEY| MW
    Web -->|REST + SSE| API
    EP --> EF --> DB

    subgraph Deploy["Deployment"]
        LOCAL[Local: start.sh<br/>API :5556 + Web :5557]
        DOCKER[Docker: Caddy + API + Web<br/>TLS on :443]
    end
```

## Key Integration Points
- **MCP → API**: All lifecycle tools route through `api-client.ts` → REST endpoints
- **Hook → API**: `check-can-complete.js` calls `/api/tasks/{id}` + `/api/tasks/{id}/test-plans` + `/api/tasks/{id}/can-complete`
- **Web → API**: SvelteKit fetches via `/api/*`, receives SSE on `/api/events?projectId=X`
- **Auth**: Browser requests bypass API key (Origin header check); MCP/CLI use `X-API-Key` header
