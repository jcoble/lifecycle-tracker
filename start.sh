#!/bin/bash
# Lifecycle Tracker — Start Script
# Starts API (port 5556) and Web (port 5557), seeds data on first run

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
API_DIR="$SCRIPT_DIR/api"
WEB_DIR="$SCRIPT_DIR/web"
DB_FILE="$API_DIR/lifecycle.db"
PIDS_FILE="$SCRIPT_DIR/.pids"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

cleanup() {
    echo ""
    echo -e "${YELLOW}Shutting down...${NC}"
    if [ -f "$PIDS_FILE" ]; then
        while read -r pid; do
            if kill -0 "$pid" 2>/dev/null; then
                kill "$pid" 2>/dev/null || true
            fi
        done < "$PIDS_FILE"
        rm -f "$PIDS_FILE"
    fi
    # Kill any child processes
    jobs -p | xargs -r kill 2>/dev/null || true
    echo -e "${GREEN}Done.${NC}"
    exit 0
}

trap cleanup SIGINT SIGTERM EXIT

echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo -e "${BLUE}  Lifecycle Tracker — Starting Up${NC}"
echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo ""

# Check prerequisites
echo -e "${YELLOW}Checking prerequisites...${NC}"
command -v dotnet >/dev/null 2>&1 || { echo -e "${RED}dotnet not found${NC}"; exit 1; }
command -v pnpm >/dev/null 2>&1 || { echo -e "${RED}pnpm not found${NC}"; exit 1; }
echo -e "${GREEN}✓ dotnet and pnpm found${NC}"

# Install web dependencies if needed
if [ ! -d "$WEB_DIR/node_modules" ]; then
    echo -e "${YELLOW}Installing web dependencies...${NC}"
    cd "$WEB_DIR" && pnpm install
fi

# Check if first run (no database)
FIRST_RUN=false
if [ ! -f "$DB_FILE" ]; then
    FIRST_RUN=true
    echo -e "${YELLOW}First run detected — will seed data after API starts${NC}"
fi

# Clear old pids
rm -f "$PIDS_FILE"

# Start API server
echo ""
echo -e "${BLUE}Starting API server on port 5556...${NC}"
cd "$API_DIR"
dotnet run --no-launch-profile > "$SCRIPT_DIR/.api.log" 2>&1 &
API_PID=$!
echo "$API_PID" > "$PIDS_FILE"
echo -e "${GREEN}✓ API started (PID: $API_PID)${NC}"

# Wait for API to be ready
echo -e "${YELLOW}Waiting for API to be ready...${NC}"
MAX_WAIT=30
WAITED=0
while ! curl -s http://localhost:5556/api/projects >/dev/null 2>&1; do
    sleep 1
    WAITED=$((WAITED + 1))
    if [ $WAITED -ge $MAX_WAIT ]; then
        echo -e "${RED}API failed to start after ${MAX_WAIT}s. Check .api.log${NC}"
        cat "$SCRIPT_DIR/.api.log" | tail -20
        exit 1
    fi
done
echo -e "${GREEN}✓ API is ready (took ${WAITED}s)${NC}"

# Seed data on first run
if [ "$FIRST_RUN" = true ]; then
    echo ""
    echo -e "${BLUE}Seeding initial data...${NC}"

    # Create default project (Origin header bypasses API key auth for browser-like requests)
    PROJECT=$(curl -s -X POST http://localhost:5556/api/projects \
        -H "Content-Type: application/json" -H "Origin: http://localhost" \
        -d '{"name": "EdiPlatform", "description": "EDI Platform — Full AS2/X12 integration system", "repository": "https://github.com/user/EdiPlatform"}')
    PROJECT_ID=$(echo "$PROJECT" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
    echo -e "${GREEN}  ✓ Created project: EdiPlatform (ID: $PROJECT_ID)${NC}"

    # Helper: all seed curls use Origin header to bypass API key auth
    SEED_HEADERS='-H "Content-Type: application/json" -H "Origin: http://localhost"'
    seed_post() { curl -s -X POST "$1" -H "Content-Type: application/json" -H "Origin: http://localhost" -d "$2"; }
    seed_patch() { curl -s -X PATCH "$1" -H "Content-Type: application/json" -H "Origin: http://localhost" -d "$2"; }

    # Create milestone
    MILESTONE=$(seed_post "http://localhost:5556/api/projects/${PROJECT_ID}/milestones" \
        '{"name": "v2.0 — Lifecycle Tracker", "description": "Build the AI-integrated lifecycle tracking system", "version": "2.0"}')
    MILESTONE_ID=$(echo "$MILESTONE" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
    echo -e "${GREEN}  ✓ Created milestone: v2.0 (ID: $MILESTONE_ID)${NC}"

    # Update milestone status
    seed_post "http://localhost:5556/api/milestones/${MILESTONE_ID}/status" '{"status": "InProgress"}' >/dev/null

    # Create phases
    PHASE1=$(seed_post "http://localhost:5556/api/milestones/${MILESTONE_ID}/phases" \
        '{"name": "Backend API", "goal": "Complete .NET API with all endpoints", "phaseNumber": 1, "orderIndex": 0, "successCriteria": "All CRUD endpoints, SSE, activity logging, file uploads"}')
    PHASE1_ID=$(echo "$PHASE1" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
    seed_patch "http://localhost:5556/api/phases/$PHASE1_ID" '{"status": "Completed"}' >/dev/null
    echo -e "${GREEN}  ✓ Created phase: Backend API (ID: $PHASE1_ID)${NC}"

    PHASE2=$(seed_post "http://localhost:5556/api/milestones/${MILESTONE_ID}/phases" \
        '{"name": "SvelteKit Frontend", "goal": "Complete UI with board, phases, metrics views", "phaseNumber": 2, "orderIndex": 1, "successCriteria": "Kanban board, all pages, dark theme, clipboard paste"}')
    PHASE2_ID=$(echo "$PHASE2" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
    seed_patch "http://localhost:5556/api/phases/$PHASE2_ID" '{"status": "Completed"}' >/dev/null
    echo -e "${GREEN}  ✓ Created phase: SvelteKit Frontend (ID: $PHASE2_ID)${NC}"

    PHASE3=$(seed_post "http://localhost:5556/api/milestones/${MILESTONE_ID}/phases" \
        '{"name": "MCP Integration", "goal": "MCP server + slash commands for Claude", "phaseNumber": 3, "orderIndex": 2, "successCriteria": "All MCP tools, slash commands, API key configured"}')
    PHASE3_ID=$(echo "$PHASE3" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
    seed_patch "http://localhost:5556/api/phases/$PHASE3_ID" '{"status": "Completed"}' >/dev/null
    echo -e "${GREEN}  ✓ Created phase: MCP Integration (ID: $PHASE3_ID)${NC}"

    PHASE4=$(seed_post "http://localhost:5556/api/milestones/${MILESTONE_ID}/phases" \
        '{"name": "Testing & Polish", "goal": "End-to-end testing and bug fixes", "phaseNumber": 4, "orderIndex": 3, "successCriteria": "All pages load, drag-drop works, CRUD works, SSE works"}')
    PHASE4_ID=$(echo "$PHASE4" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
    seed_patch "http://localhost:5556/api/phases/$PHASE4_ID" '{"status": "InProgress"}' >/dev/null
    echo -e "${GREEN}  ✓ Created phase: Testing & Polish (ID: $PHASE4_ID)${NC}"

    # Create sample tasks
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE4_ID, \"title\": \"Verify all API endpoints\", \"status\": \"Todo\", \"priority\": \"P1\", \"type\": \"Test\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE4_ID, \"title\": \"Test kanban board drag-and-drop\", \"status\": \"Todo\", \"priority\": \"P1\", \"type\": \"Test\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE4_ID, \"title\": \"Test clipboard screenshot paste\", \"status\": \"Todo\", \"priority\": \"P2\", \"type\": \"Test\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE4_ID, \"title\": \"Verify SSE real-time updates\", \"status\": \"Backlog\", \"priority\": \"P2\", \"type\": \"Test\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE1_ID, \"title\": \"Build all entity models\", \"status\": \"Done\", \"priority\": \"P1\", \"type\": \"Feature\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE1_ID, \"title\": \"Implement CRUD endpoints\", \"status\": \"Done\", \"priority\": \"P1\", \"type\": \"Feature\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE2_ID, \"title\": \"Create kanban board with drag-and-drop\", \"status\": \"Done\", \"priority\": \"P1\", \"type\": \"Feature\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks "{\"phaseId\": $PHASE2_ID, \"title\": \"Implement dark theme UI\", \"status\": \"Done\", \"priority\": \"P2\", \"type\": \"Feature\", \"source\": \"Claude\"}" >/dev/null
    seed_post http://localhost:5556/api/tasks '{"title": "Add chart library for metrics page", "status": "Backlog", "priority": "P4", "type": "Feature", "source": "Manual"}' >/dev/null
    seed_post http://localhost:5556/api/tasks '{"title": "Fix sidebar active link on phase detail", "status": "Backlog", "priority": "P3", "type": "Bug", "source": "Manual"}' >/dev/null
    echo -e "${GREEN}  ✓ Created 10 sample tasks${NC}"

    # Labels are auto-created by the project creation endpoint (default labels)
    # Only add the edi-platform label which isn't in the defaults
    seed_post "http://localhost:5556/api/projects/${PROJECT_ID}/labels" '{"name": "edi-platform", "color": "#06b6d4"}' >/dev/null
    echo -e "${GREEN}  ✓ Default labels created via project endpoint + 1 custom label${NC}"

    echo -e "${GREEN}Seed data complete!${NC}"
fi

# Start web dev server
echo ""
echo -e "${BLUE}Starting web dev server on port 5557...${NC}"
cd "$WEB_DIR"
pnpm dev > "$SCRIPT_DIR/.web.log" 2>&1 &
WEB_PID=$!
echo "$WEB_PID" >> "$PIDS_FILE"
echo -e "${GREEN}✓ Web started (PID: $WEB_PID)${NC}"

# Wait for web to be ready
echo -e "${YELLOW}Waiting for web server to be ready...${NC}"
WAITED=0
while ! curl -s http://localhost:5557 >/dev/null 2>&1; do
    sleep 1
    WAITED=$((WAITED + 1))
    if [ $WAITED -ge $MAX_WAIT ]; then
        echo -e "${RED}Web server failed to start after ${MAX_WAIT}s. Check .web.log${NC}"
        cat "$SCRIPT_DIR/.web.log" | tail -20
        exit 1
    fi
done
echo -e "${GREEN}✓ Web is ready (took ${WAITED}s)${NC}"

echo ""
echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo -e "${GREEN}  Lifecycle Tracker is running!${NC}"
echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo ""
echo -e "  ${BLUE}Web UI:${NC}  http://localhost:5557"
echo -e "  ${BLUE}API:${NC}     http://localhost:5556"
echo -e "  ${BLUE}SSE:${NC}     http://localhost:5556/api/events?projectId=1"
echo ""
echo -e "  ${YELLOW}Press Ctrl+C to stop all services${NC}"
echo ""

# Keep running
wait
