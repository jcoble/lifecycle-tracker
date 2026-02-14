import { readdirSync, statSync } from 'fs';
import { join } from 'path';

let _discoveredLogPath: string | null = null;

/** Discover the current Claude Code JSONL transcript path by convention. */
export function discoverSessionLogPath(): string | null {
  if (_discoveredLogPath) return _discoveredLogPath;
  try {
    const cwd = process.cwd();
    // Claude Code encodes cwd: /Users/foo/bar → -Users-foo-bar
    const encoded = cwd.replace(/\//g, '-');
    const homeDir = process.env.HOME || process.env.USERPROFILE || '';
    const projectsDir = join(homeDir, '.claude', 'projects', encoded);
    const files = readdirSync(projectsDir)
      .filter(f => f.endsWith('.jsonl'))
      .map(f => ({
        name: f,
        path: join(projectsDir, f),
        mtime: statSync(join(projectsDir, f)).mtimeMs,
      }))
      .sort((a, b) => b.mtime - a.mtime);
    if (files.length > 0) {
      _discoveredLogPath = files[0].path;
    }
  } catch {
    // Directory doesn't exist or not accessible
  }
  return _discoveredLogPath;
}

/** Reset cached path (useful if session changes). */
export function resetDiscoveredLogPath() {
  _discoveredLogPath = null;
}
