#!/usr/bin/env node

/**
 * PreToolUse hook for complete_task enforcement.
 *
 * Workflow:
 * 1. Docs/Infra/Research tasks → ALLOW (exempt)
 * 2. Test tasks → ALLOW (test agent completes these after browser testing)
 * 3. Feature/Bug/Refactor tasks:
 *    - Must be in Review status (not InProgress) → BLOCK if not
 *    - If skipUiTesting=true → ALLOW
 *    - All non-UI tests on this task must be Passing → BLOCK if failing/unrun
 *    - API-level CanCompleteDone checks all tests on this task (no linked Test task needed)
 *
 * Exit code 2 = block the tool call (with reason on stderr).
 * Exit code 0 = allow the tool call.
 */

const fs = require('fs');
const path = require('path');

const API_URL = process.env.LIFECYCLE_API_URL || 'http://localhost:5556';

const EXEMPT_TYPES = ['Docs', 'Infra', 'Research'];
const REVIEW_REQUIRED_TYPES = ['Feature', 'Bug', 'Refactor'];

function getApiKey() {
  if (process.env.LIFECYCLE_API_KEY) return process.env.LIFECYCLE_API_KEY;
  try {
    const keyPath = path.join(process.env.HOME || '', '.lifecycle-api-key');
    return fs.readFileSync(keyPath, 'utf-8').trim();
  } catch {
    return '';
  }
}

async function main() {
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
  }

  let hookData;
  try {
    hookData = JSON.parse(input);
  } catch {
    process.exit(0);
  }

  const taskId = hookData?.tool_input?.taskId;
  if (!taskId) {
    process.exit(0);
  }

  const apiKey = getApiKey();
  const headers = { 'Content-Type': 'application/json', 'X-API-Key': apiKey };

  try {
    // Fetch the task
    const taskRes = await fetch(`${API_URL}/api/tasks/${taskId}`, { headers });
    if (!taskRes.ok) {
      process.exit(0); // API error, don't block
    }
    const task = await taskRes.json();

    // 1. Exempt types
    if (EXEMPT_TYPES.includes(task.type)) {
      process.exit(0);
    }

    // 2. Test tasks - allow (test agent completes these)
    if (task.type === 'Test') {
      process.exit(0);
    }

    // 3. Feature/Bug/Refactor enforcement
    if (REVIEW_REQUIRED_TYPES.includes(task.type)) {
      // Must be in Review status
      if (task.status !== 'Review') {
        process.stderr.write(
          `BLOCKED: Task #${taskId} "${task.title}" is in ${task.status} status.\n\n` +
          `Feature/Bug/Refactor tasks must be in Review status before completion.\n` +
          `Use request_review to submit this task for review first.\n`
        );
        process.exit(2);
      }

      // skipUiTesting only skips UI test checks, still check non-UI tests

      // Enforce: must have at least one test
      const allTests = task.tests || [];
      if (allTests.length === 0) {
        process.stderr.write(
          `BLOCKED: Task #${taskId} "${task.title}" has no tests registered.\n\n` +
          `Feature/Bug/Refactor tasks must have tests. Use request_review with\n` +
          `backendTests[] and/or testPlan to register tests before completing.\n`
        );
        process.exit(2);
      }

      // Check non-UI tests are passing
      const tests = task.tests || [];
      const nonUiTests = tests.filter(t => (t.testType || t.type) !== 'UI');
      const failing = nonUiTests.filter(t => t.status === 'Failing');
      const notRun = nonUiTests.filter(t => t.status === 'Created' || t.status === 'NotCreated');

      if (failing.length > 0) {
        process.stderr.write(
          `BLOCKED: Task #${taskId} has ${failing.length} failing non-UI test(s).\n` +
          `Fix failing tests before completing the task.\n`
        );
        process.exit(2);
      }
      if (notRun.length > 0) {
        process.stderr.write(
          `BLOCKED: Task #${taskId} has ${notRun.length} non-UI test(s) that haven't been run.\n` +
          `Run all tests before completing the task.\n`
        );
        process.exit(2);
      }

      // All test checks are handled by the API-level CanCompleteDone validator
      // (checks all tests on THIS task are passing — no linked Test task needed)
    }

    // If RequiredTestLevel is set, also check can-complete API
    if (task.requiredTestLevel) {
      const canRes = await fetch(`${API_URL}/api/tasks/${taskId}/can-complete?skipUiCheck=true`, { headers });
      if (canRes.ok) {
        const result = await canRes.json();
        if (result.canComplete === false) {
          process.stderr.write(
            `BLOCKED: ${result.reason || 'Test plan requirements not met'}\n`
          );
          process.exit(2);
        }
      }
    }

    process.exit(0);
  } catch {
    process.exit(0); // Network error, don't block
  }
}

main();
