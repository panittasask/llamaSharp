import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable, firstValueFrom } from 'rxjs';

export interface PromptRequest {
  prompt: string;
}

export interface ServerStatus {
  isRunning: boolean;
  processId: number | null;
  currentModelPath: string | null;
  baseUrl: string | null;
  message: string | null;
}

export interface ModelsResponse {
  count: number;
  models: string[];
}

export interface NormalResponse {
  response: string;
  timeElapsedMs: number;
  timeElapsedSec: number;
}

export interface AppServerState {
  status: ServerStatus | null;
  models: string[];
  selectedModel: string;
}

export interface ServerConfigResponse {
  contextSize: number;
  maxTokens: number;
}

export interface AgentChecklistItem {
  id: string;
  title: string;
  description: string;
  isDone: boolean;
}

export interface AgentSessionEvent {
  id: string;
  activity: string;
  detail: string;
  status: string;
  timestampUtc: string;
}

export interface AgentPlanState {
  title: string;
  goal: string;
  activeFolderPath: string;
  availableFolders: string[];
  availableSkills: string[];
  availableRules: string[];
  selectedSkills: string[];
  selectedRules: string[];
  fileCreationMode: 'ask-path' | 'auto-create';
  defaultCreateFolder: string;
  checklist: AgentChecklistItem[];
  lastIndexFile: string | null;
  lastEmbeddingFile: string | null;
  sessionId: string;
  recentEvents: AgentSessionEvent[];
  updatedAtUtc: string;
}

export interface RunTestsResult {
  passed: boolean;
  attempts: number;
  command: string;
  output: string;
  startedAtUtc: string;
  endedAtUtc: string;
}

export interface PromptFileActionResult {
  requiresPath: boolean;
  created: boolean;
  message: string;
  createdFilePath: string | null;
  suggestedFolder: string | null;
}

export interface AgentPipelineCycleLog {
  cycle: number;
  passed: boolean;
  summary: string;
}

export interface AgentPipelineResult {
  completed: boolean;
  cyclesUsed: number;
  planFilePath: string;
  checklistFilePath: string;
  lastErrorSummary: string | null;
  lastTestOutput: string;
  cycleLogs: AgentPipelineCycleLog[];
}

export interface WebSearchResult {
  title: string;
  url: string;
  snippet: string;
  source: string;
}

export interface WebSearchResponse {
  provider: string;
  query: string;
  count: number;
  results: WebSearchResult[];
}

@Injectable({
  providedIn: 'root',
})
export class LlmApiService {
  private static readonly STORAGE_BASE_KEY = 'llm.apiBaseUrl';
  private readonly defaultBase = 'http://localhost:5054/api/Llm';

  private readonly apiBaseSubject = new BehaviorSubject<string>(
    this.loadInitialBaseUrl(),
  );
  readonly apiBaseUrl$ = this.apiBaseSubject.asObservable();

  private readonly appStateSubject = new BehaviorSubject<AppServerState>({
    status: null,
    models: [],
    selectedModel: '',
  });
  readonly appState$ = this.appStateSubject.asObservable();

  constructor(private readonly http: HttpClient) {}

  getApiBaseUrl(): string {
    return this.apiBaseSubject.value;
  }

  updateApiBaseUrl(rawUrl: string): void {
    const next = (rawUrl || '').trim().replace(/\/$/, '');
    const safeUrl = next || this.defaultBase;
    this.apiBaseSubject.next(safeUrl);
    try {
      localStorage.setItem(LlmApiService.STORAGE_BASE_KEY, safeUrl);
    } catch {
      // Ignore storage errors in restricted browser contexts.
    }
  }

  getCurrentState(): AppServerState {
    return this.appStateSubject.value;
  }

  async refreshAppState(): Promise<AppServerState> {
    const [status, modelData] = await Promise.all([
      firstValueFrom(this.getServerStatus()),
      firstValueFrom(this.getModels()),
    ]);

    const models = modelData.models ?? [];
    let selectedModel = this.appStateSubject.value.selectedModel;
    if (status?.currentModelPath) {
      selectedModel = status.currentModelPath;
    } else if (!selectedModel && models.length > 0) {
      selectedModel = models[0];
    }

    const nextState: AppServerState = {
      status,
      models,
      selectedModel,
    };
    this.appStateSubject.next(nextState);
    return nextState;
  }

  setSelectedModel(modelPath: string): void {
    this.appStateSubject.next({
      ...this.appStateSubject.value,
      selectedModel: modelPath,
    });
  }

  updateStatus(status: ServerStatus): void {
    const current = this.appStateSubject.value;
    const selectedModel = status.currentModelPath || current.selectedModel;
    this.appStateSubject.next({
      ...current,
      status,
      selectedModel,
    });
  }

  private loadInitialBaseUrl(): string {
    try {
      const stored = localStorage
        .getItem(LlmApiService.STORAGE_BASE_KEY)
        ?.trim();
      if (stored) {
        return stored.replace(/\/$/, '');
      }
    } catch {
      // Ignore storage errors in restricted browser contexts.
    }

    return this.defaultBase;
  }

  private endpoint(path: string): string {
    return `${this.apiBaseSubject.value}${path}`;
  }

  private agentEndpoint(path: string): string {
    const base = this.apiBaseSubject.value.replace(/\/$/, '');
    const root = base.replace(/\/api\/.+$/i, '');
    return `${root}/api/AgentWorkspace${path}`;
  }

  getServerStatus(): Observable<ServerStatus> {
    return this.http.get<ServerStatus>(this.endpoint('/server/status'));
  }

  getModels(): Observable<ModelsResponse> {
    return this.http.get<ModelsResponse>(this.endpoint('/server/models'));
  }

  startServer(modelPath?: string): Observable<ServerStatus> {
    return this.http.post<ServerStatus>(this.endpoint('/server/start'), {
      modelPath,
    });
  }

  stopServer(): Observable<ServerStatus> {
    return this.http.post<ServerStatus>(this.endpoint('/server/stop'), {});
  }

  switchModel(modelPath: string): Observable<ServerStatus> {
    return this.http.post<ServerStatus>(this.endpoint('/server/switch-model'), {
      modelPath,
    });
  }

  getServerConfig(): Observable<ServerConfigResponse> {
    return this.http.get<ServerConfigResponse>(this.endpoint('/server/config'));
  }

  chatNormal(prompt: string): Observable<NormalResponse> {
    const payload: PromptRequest = { prompt };
    return this.http.post<NormalResponse>(this.endpoint('/normal'), payload);
  }

  getStreamUrl(): string {
    return this.endpoint('/stream');
  }

  getAgentPlanState(): Observable<AgentPlanState> {
    return this.http.get<AgentPlanState>(this.agentEndpoint('/state'));
  }

  bootstrapAgentWorkspace(): Observable<AgentPlanState> {
    return this.http.post<AgentPlanState>(this.agentEndpoint('/bootstrap'), {});
  }

  updateChecklistItem(
    itemId: string,
    isDone: boolean,
  ): Observable<AgentPlanState> {
    return this.http.post<AgentPlanState>(
      this.agentEndpoint(`/checklist/${encodeURIComponent(itemId)}`),
      { isDone },
    );
  }

  updateActiveFolder(folderPath: string): Observable<AgentPlanState> {
    return this.http.post<AgentPlanState>(
      this.agentEndpoint('/active-folder'),
      {
        folderPath,
      },
    );
  }

  rebuildAgentIndex(folderPath?: string): Observable<AgentPlanState> {
    return this.http.post<AgentPlanState>(
      this.agentEndpoint('/rebuild-index'),
      {
        folderPath,
      },
    );
  }

  selectGuidance(
    selectedSkills: string[],
    selectedRules: string[],
  ): Observable<AgentPlanState> {
    return this.http.post<AgentPlanState>(
      this.agentEndpoint('/guidance/select'),
      {
        selectedSkills,
        selectedRules,
      },
    );
  }

  runTestsTool(command?: string, maxAttempts = 3): Observable<RunTestsResult> {
    return this.http.post<RunTestsResult>(
      this.agentEndpoint('/tools/run-tests'),
      {
        command,
        maxAttempts,
      },
    );
  }

  updateFileCreateSettings(
    mode: 'ask-path' | 'auto-create',
    defaultFolder: string,
  ): Observable<AgentPlanState> {
    return this.http.post<AgentPlanState>(
      this.agentEndpoint('/settings/file-create'),
      {
        mode,
        defaultFolder,
      },
    );
  }

  createFileFromPrompt(
    prompt: string,
    targetPath?: string,
  ): Observable<PromptFileActionResult> {
    return this.http.post<PromptFileActionResult>(
      this.agentEndpoint('/tools/create-file-from-prompt'),
      {
        prompt,
        targetPath,
      },
    );
  }

  runAgentPipeline(payload: {
    goal: string;
    executorPrompt?: string;
    targetPath?: string;
    testCommand?: string;
    maxCycles?: number;
  }): Observable<AgentPipelineResult> {
    return this.http.post<AgentPipelineResult>(
      this.agentEndpoint('/agent/pipeline'),
      payload,
    );
  }

  searchWeb(query: string, limit = 5): Observable<WebSearchResponse> {
    return this.http.get<WebSearchResponse>(this.agentEndpoint('/search-web'), {
      params: {
        query,
        limit,
      },
    });
  }
}
