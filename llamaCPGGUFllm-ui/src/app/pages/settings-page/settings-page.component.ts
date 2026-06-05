import { CommonModule } from '@angular/common';
import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import {
  AgentPlanState,
  AiProviderStatus,
  PromptFileActionResult,
  AgentSessionEvent,
  LmStudioModelsResponse,
  LlmApiService,
  RunTestsResult,
  WebSearchResult,
} from '../../llm-api.service';

@Component({
  selector: 'app-settings-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './settings-page.component.html',
  styleUrl: './settings-page.component.scss',
})
export class SettingsPageComponent {
  activeTab: 'server' | 'agent' | 'tools' | 'prompt' = 'server';

  apiBaseUrl = '';
  statusText = '';
  serverBusy = false;
  providerBusy = false;
  agentBusy = false;
  providerStatus: AiProviderStatus | null = null;
  lmStudioModels: string[] = [];
  lmStudioMessage = '';
  selectedLmStudioModel = '';
  openAiModelInput = '';

  agentState: AgentPlanState | null = null;
  selectedAgentFolder = '';
  searchQuery = '';
  searchResults: WebSearchResult[] = [];
  searchBusy = false;
  selectedSkills: string[] = [];
  selectedRules: string[] = [];
  runTestsBusy = false;
  runTestsCommand = 'dotnet test llamaCPGGUFllm.sln';
  runTestsMaxAttempts = 3;
  runTestsResult: RunTestsResult | null = null;

  fileCreateMode: 'ask-path' | 'auto-create' = 'ask-path';
  defaultCreateFolder = 'agent-workspace/generated';
  promptCreateInput = '';
  promptCreateTargetPath = '';
  promptCreateBusy = false;
  promptCreateResult: PromptFileActionResult | null = null;

  currentActivity: 'idle' | 'thinking' | 'acting' | 'create plan' = 'idle';
  activityText = 'Idle';

  constructor(public readonly api: LlmApiService) {}

  async ngOnInit(): Promise<void> {
    await this.refreshAll();
  }

  async refreshAll(): Promise<void> {
    this.statusText = '';
    try {
      await Promise.all([
        this.api.refreshAppState(),
        this.refreshAgentState(),
        this.refreshProviderAndModels(),
      ]);
      this.apiBaseUrl = this.api.getApiBaseUrl();
    } catch (err) {
      this.statusText = this.formatError(err);
    }
  }

  private async refreshAgentState(): Promise<void> {
    const state = await firstValueFrom(this.api.getAgentPlanState());
    this.agentState = state;
    this.selectedAgentFolder = state.activeFolderPath;
    this.selectedSkills = [...state.selectedSkills];
    this.selectedRules = [...state.selectedRules];
    this.fileCreateMode = state.fileCreationMode || 'ask-path';
    this.defaultCreateFolder =
      state.defaultCreateFolder || 'agent-workspace/generated';
  }

  selectTab(tab: 'server' | 'agent' | 'tools' | 'prompt'): void {
    this.activeTab = tab;
  }

  saveApiBaseUrl(): void {
    this.api.updateApiBaseUrl(this.apiBaseUrl);
    this.statusText = 'Saved API base URL';
    void this.refreshProviderAndModels();
  }

  async refreshProviderAndModels(): Promise<void> {
    this.providerBusy = true;
    this.lmStudioMessage = '';
    try {
      const provider = await firstValueFrom(this.api.getAiProviderStatus());
      this.providerStatus = provider;
      this.openAiModelInput = provider.configuredModel || '';

      if (!provider.isOpenAiCompatible) {
        this.lmStudioModels = [];
        this.selectedLmStudioModel = '';
        this.lmStudioMessage =
          'Provider is not openai, so LM Studio model lookup is skipped.';
        return;
      }

      const modelResponse: LmStudioModelsResponse = await firstValueFrom(
        this.api.getLmStudioModels(),
      );
      this.lmStudioModels = modelResponse.models ?? [];
      this.selectedLmStudioModel =
        this.lmStudioModels.find((m) => m === this.openAiModelInput) ||
        this.selectedLmStudioModel ||
        '';
      this.lmStudioMessage =
        modelResponse.message ||
        `Loaded ${modelResponse.count} model(s) from /v1/models`;
    } catch (err) {
      this.lmStudioMessage = this.formatError(err);
    } finally {
      this.providerBusy = false;
    }
  }

  async startServer(): Promise<void> {
    this.serverBusy = true;
    this.statusText = '';
    try {
      const result = await firstValueFrom(
        this.api.startServer(
          this.api.getCurrentState().selectedModel || undefined,
        ),
      );
      this.api.updateStatus(result);
      this.statusText = result.message ?? 'Server started';
      await this.api.refreshAppState();
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.serverBusy = false;
    }
  }

  selectLmStudioModel(model: string): void {
    this.selectedLmStudioModel = model;
    this.openAiModelInput = model;
  }

  async saveOpenAiModel(): Promise<void> {
    const model = this.openAiModelInput.trim();
    if (!model) {
      this.statusText = 'OpenAI model is required';
      return;
    }

    this.providerBusy = true;
    this.statusText = '';
    try {
      const status = await firstValueFrom(this.api.setOpenAiModel(model));
      this.providerStatus = status;
      this.statusText = `Active OpenAI model set to ${status.configuredModel || model}`;
      await this.refreshProviderAndModels();
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.providerBusy = false;
    }
  }

  async stopServer(): Promise<void> {
    this.serverBusy = true;
    this.statusText = '';
    try {
      const result = await firstValueFrom(this.api.stopServer());
      this.api.updateStatus(result);
      this.statusText = result.message ?? 'Server stopped';
      await this.api.refreshAppState();
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.serverBusy = false;
    }
  }

  async switchModel(): Promise<void> {
    const selectedModel = this.api.getCurrentState().selectedModel.trim();
    if (!selectedModel) {
      this.statusText = 'Select a model first';
      return;
    }

    this.serverBusy = true;
    this.statusText = '';
    try {
      const result = await firstValueFrom(this.api.switchModel(selectedModel));
      this.api.updateStatus(result);
      this.statusText = result.message ?? 'Model switched';
      await this.api.refreshAppState();
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.serverBusy = false;
    }
  }

  setSelectedModel(modelPath: string): void {
    this.api.setSelectedModel(modelPath);
  }

  async bootstrapAgentWorkspace(): Promise<void> {
    this.agentBusy = true;
    this.setActivity('create plan', 'Creating workspace plan and checklist...');
    try {
      const state = await firstValueFrom(this.api.bootstrapAgentWorkspace());
      this.agentState = state;
      this.selectedAgentFolder = state.activeFolderPath;
      this.selectedSkills = [...state.selectedSkills];
      this.selectedRules = [...state.selectedRules];
      this.statusText = 'Agent workspace bootstrap completed';
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.agentBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  async toggleChecklist(itemId: string, isDone: boolean): Promise<void> {
    this.agentBusy = true;
    this.setActivity('acting', 'Updating checklist item...');
    try {
      const state = await firstValueFrom(
        this.api.updateChecklistItem(itemId, isDone),
      );
      this.agentState = state;
      this.selectedAgentFolder = state.activeFolderPath;
      this.selectedSkills = [...state.selectedSkills];
      this.selectedRules = [...state.selectedRules];
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.agentBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  async saveAgentFolder(): Promise<void> {
    if (!this.selectedAgentFolder.trim()) {
      this.statusText = 'Select or type a folder path first';
      return;
    }

    this.agentBusy = true;
    this.setActivity('acting', 'Switching active folder...');
    try {
      const state = await firstValueFrom(
        this.api.updateActiveFolder(this.selectedAgentFolder.trim()),
      );
      this.agentState = state;
      this.selectedAgentFolder = state.activeFolderPath;
      this.selectedSkills = [...state.selectedSkills];
      this.selectedRules = [...state.selectedRules];
      this.statusText = 'Active folder updated';
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.agentBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  async rebuildIndex(): Promise<void> {
    this.agentBusy = true;
    this.setActivity('acting', 'Indexing files and generating embeddings...');
    try {
      const state = await firstValueFrom(
        this.api.rebuildAgentIndex(this.selectedAgentFolder || undefined),
      );
      this.agentState = state;
      this.selectedAgentFolder = state.activeFolderPath;
      this.selectedSkills = [...state.selectedSkills];
      this.selectedRules = [...state.selectedRules];
      this.statusText = 'Index and embedding store updated';
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.agentBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  async saveGuidanceSelection(): Promise<void> {
    this.agentBusy = true;
    this.setActivity('acting', 'Applying selected skills and rules...');
    try {
      const state = await firstValueFrom(
        this.api.selectGuidance(this.selectedSkills, this.selectedRules),
      );
      this.agentState = state;
      this.selectedSkills = [...state.selectedSkills];
      this.selectedRules = [...state.selectedRules];
      this.statusText = 'Guidance selection saved';
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.agentBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  async runTestsTool(): Promise<void> {
    this.runTestsBusy = true;
    this.setActivity('acting', 'Running tests with retry loop...');
    try {
      const result = await firstValueFrom(
        this.api.runTestsTool(
          this.runTestsCommand.trim() || undefined,
          this.runTestsMaxAttempts,
        ),
      );
      this.runTestsResult = result;
      await this.refreshAgentState();
      this.statusText = result.passed
        ? `Tests passed in ${result.attempts} attempt(s)`
        : `Tests still failing after ${result.attempts} attempt(s)`;
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.runTestsBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  async saveFileCreateSettings(): Promise<void> {
    this.agentBusy = true;
    this.setActivity('acting', 'Saving prompt file-create settings...');
    try {
      const state = await firstValueFrom(
        this.api.updateFileCreateSettings(
          this.fileCreateMode,
          this.defaultCreateFolder.trim() || 'agent-workspace/generated',
        ),
      );
      this.agentState = state;
      this.fileCreateMode = state.fileCreationMode;
      this.defaultCreateFolder = state.defaultCreateFolder;
      this.statusText = 'File creation settings saved';
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.agentBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  async runPromptCreateFile(): Promise<void> {
    const prompt = this.promptCreateInput.trim();
    if (!prompt) {
      this.statusText = 'Type prompt for file creation first';
      return;
    }

    this.promptCreateBusy = true;
    this.setActivity('thinking', 'Handling file creation from prompt...');
    try {
      const result = await firstValueFrom(
        this.api.createFileFromPrompt(
          prompt,
          this.promptCreateTargetPath.trim() || undefined,
        ),
      );
      this.promptCreateResult = result;
      await this.refreshAgentState();
      this.statusText = result.message;
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.promptCreateBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  onSkillsChange(values: string[]): void {
    this.selectedSkills = values;
  }

  onRulesChange(values: string[]): void {
    this.selectedRules = values;
  }

  async runWebSearch(): Promise<void> {
    const query = this.searchQuery.trim();
    if (!query) {
      this.statusText = 'Type search keywords first';
      return;
    }

    this.searchBusy = true;
    this.setActivity('thinking', 'Searching internet via DuckDuckGo...');
    try {
      const response = await firstValueFrom(this.api.searchWeb(query, 6));
      this.searchResults = response.results;
    } catch (err) {
      this.statusText = this.formatError(err);
    } finally {
      this.searchBusy = false;
      this.setActivity('idle', 'Idle');
    }
  }

  trackEvent(_: number, event: AgentSessionEvent): string {
    return event.id;
  }

  private setActivity(
    activity: 'idle' | 'thinking' | 'acting' | 'create plan',
    text: string,
  ): void {
    this.currentActivity = activity;
    this.activityText = text;
  }

  private formatError(err: unknown): string {
    if (err instanceof Error) {
      return err.message;
    }

    if (typeof err === 'string') {
      return err;
    }

    return 'Unknown error';
  }
}
