import { CommonModule } from '@angular/common';
import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { LlmApiService, PromptFileActionResult } from '../../llm-api.service';

@Component({
  selector: 'app-agent-workflow-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './agent-workflow-page.component.html',
  styleUrl: './agent-workflow-page.component.scss',
})
export class AgentWorkflowPageComponent {
  filePath = '';
  filePrompt = '';

  createBusy = false;
  createResult: PromptFileActionResult | null = null;

  backgroundWorkflowBusy = false;
  backgroundWorkflowStatus = '';
  statusText = '';

  constructor(private readonly api: LlmApiService) {}

  async createFileNow(): Promise<void> {
    const prompt = this.filePrompt.trim();
    const targetPath = this.filePath.trim();
    if (!prompt) {
      this.statusText = 'Prompt is required';
      return;
    }

    if (!targetPath) {
      this.statusText = 'File path is required for direct create mode';
      return;
    }

    this.createBusy = true;
    this.statusText = 'Creating file...';
    this.createResult = null;

    try {
      const result = await firstValueFrom(
        this.api.createFileFromPrompt(prompt, targetPath),
      );
      this.createResult = result;
      this.statusText = result.message;

      // Keep workflow internal: planner/executor/tester runs in background.
      this.backgroundWorkflowStatus = 'Background workflow started...';
      void this.runBackgroundWorkflow(prompt, targetPath);
    } catch (err) {
      this.statusText =
        err instanceof Error ? err.message : 'Failed to create file';
    } finally {
      this.createBusy = false;
    }
  }

  private async runBackgroundWorkflow(
    goalPrompt: string,
    targetPath: string,
  ): Promise<void> {
    this.backgroundWorkflowBusy = true;
    try {
      const result = await firstValueFrom(
        this.api.runAgentPipeline({
          goal: goalPrompt,
          executorPrompt: goalPrompt,
          targetPath,
          testCommand: 'dotnet test llamaCPGGUFllm.sln',
          maxCycles: 3,
        }),
      );

      this.backgroundWorkflowStatus = result.completed
        ? `Background workflow passed in ${result.cyclesUsed} cycle(s)`
        : `Background workflow stopped after ${result.cyclesUsed} cycle(s): ${result.lastErrorSummary ?? 'unknown error'}`;
    } catch (err) {
      this.backgroundWorkflowStatus =
        err instanceof Error
          ? `Background workflow failed: ${err.message}`
          : 'Background workflow failed';
    } finally {
      this.backgroundWorkflowBusy = false;
    }
  }
}
