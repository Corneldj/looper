import { Injectable } from '@angular/core';

/** Hands the browser a file to save. A service so specs can stub it instead of clicking real anchors. */
@Injectable({ providedIn: 'root' })
export class FileDownloads {
  saveBlob(fileName: string, blob: Blob): void {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}

/** Mirrors the API's file naming: "Growth loops" → "growth-loops.workflow". */
export function workflowFileName(workflowName: string): string {
  const slug = workflowName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return `${slug || 'workflow'}.workflow`;
}
