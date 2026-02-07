#!/bin/bash
API_URL="http://localhost:5556"
API_KEY=$(cat ~/.lifecycle-api-key 2>/dev/null || echo "")
AUTH=""
if [ -n "$API_KEY" ]; then AUTH="-H \"X-API-Key: $API_KEY\""; fi

# Create default project
curl -s -X POST "$API_URL/api/projects" \
  -H "Content-Type: application/json" \
  -d '{"name": "EdiPlatform", "description": "EDI Platform - Full AS2/X12 integration system", "repository": "https://github.com/user/EdiPlatform"}'

# Create initial milestone
curl -s -X POST "$API_URL/api/projects/1/milestones" \
  -H "Content-Type: application/json" \
  -d '{"name": "v2.0 - Lifecycle Tracker", "description": "Build the lifecycle tracking system", "version": "2.0", "status": "InProgress"}'

echo "Seed data created!"
