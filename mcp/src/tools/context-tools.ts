import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api } from '../api-client.js';

interface AttachmentMeta {
  id: number;
  taskId: number;
  fileName: string;
  originalFileName: string;
  contentType: string;
  fileSize: number;
  width?: number;
  height?: number;
  uploadedBy: string;
  uploadedAt: string;
}

interface AttachmentBase64 {
  id: number;
  taskId: number;
  originalFileName: string;
  contentType: string;
  width?: number;
  height?: number;
  base64Data: string;
}

export function registerContextTools(server: McpServer) {
  server.tool(
    'upload_screenshot',
    'Upload a base64-encoded screenshot to a task',
    {
      taskId: z.number().describe('Task ID'),
      base64Data: z.string().describe('Base64-encoded image data'),
      fileName: z.string().default('screenshot.png').describe('File name'),
      contentType: z.string().default('image/png').describe('MIME type'),
    },
    async ({ taskId, base64Data, fileName, contentType }) => {
      const result = await api.post('/ai/attachments/paste', { taskId, base64Data, fileName, contentType });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result) }] };
    }
  );

  server.tool(
    'get_task_screenshots',
    'Get screenshots/attachments for a task. Returns images that Claude can see directly.',
    {
      taskId: z.number().describe('Task ID to get screenshots for'),
    },
    async ({ taskId }) => {
      const attachments = await api.get<AttachmentMeta[]>(`/tasks/${taskId}/attachments`);

      if (!attachments || attachments.length === 0) {
        return { content: [{ type: 'text' as const, text: 'No attachments found for this task.' }] };
      }

      const imageAttachments = attachments.filter(a => a.contentType.startsWith('image/'));
      const nonImageAttachments = attachments.filter(a => !a.contentType.startsWith('image/'));

      const content: Array<{ type: 'text'; text: string } | { type: 'image'; data: string; mimeType: string }> = [];

      // Summary text
      const summary = `Task #${taskId} has ${attachments.length} attachment(s): ${imageAttachments.length} image(s), ${nonImageAttachments.length} other file(s).`;
      content.push({ type: 'text' as const, text: summary });

      // Fetch and return each image
      for (const img of imageAttachments) {
        try {
          const data = await api.get<AttachmentBase64>(`/attachments/${img.id}/base64`);
          content.push({ type: 'text' as const, text: `\n[${img.originalFileName}] (${img.contentType}, ${formatSize(img.fileSize)})` });
          content.push({ type: 'image' as const, data: data.base64Data, mimeType: data.contentType });
        } catch {
          content.push({ type: 'text' as const, text: `\n[${img.originalFileName}] — failed to load image` });
        }
      }

      // List non-image attachments
      for (const file of nonImageAttachments) {
        content.push({ type: 'text' as const, text: `\n[${file.originalFileName}] (${file.contentType}, ${formatSize(file.fileSize)}) — non-image, cannot display` });
      }

      return { content };
    }
  );
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes}B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)}KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
}
