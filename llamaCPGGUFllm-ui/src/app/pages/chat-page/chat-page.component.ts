import { CommonModule } from '@angular/common';
import {
  AfterViewChecked,
  Component,
  DoCheck,
  ElementRef,
  OnInit,
  ViewChild,
} from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import {
  LlmApiService,
  NormalResponse,
  ServerConfigResponse,
  ChatMessagePayload,
  WebSearchResult,
} from '../../llm-api.service';
import {
  ChatMessage,
  ChatSession,
  ChatSessionService,
} from '../../chat-session.service';

type ComposerMode = 'chat' | 'agent';
type ChatSpeedMode = 'thinking' | 'fast';
const DEFAULT_CONTEXT_SIZE = 4096;
const DEFAULT_MAX_TOKENS = 1024;
const STREAM_FLUSH_INTERVAL_MS = 10;
const RECENT_WINDOW_MIN_TURNS = 4;
const RECENT_WINDOW_MAX_TURNS = 8;
const CONTEXT_SAFETY_MARGIN_RATIO = 0.8;

@Component({
  selector: 'app-chat-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './chat-page.component.html',
  styleUrl: './chat-page.component.scss',
})
export class ChatPageComponent implements AfterViewChecked, DoCheck, OnInit {
  @ViewChild('chatScrollFrame')
  private readonly chatScrollFrame?: ElementRef<HTMLDivElement>;

  prompt = '';
  agentFilePath = '';
  composerMode: ComposerMode = 'chat';
  messages: ChatMessage[] = [];

  normalBusy = false;
  streamBusy = false;
  agentBusy = false;
  searchBusy = false;
  showSearch = false;
  searchQuery = '';
  searchResults: WebSearchResult[] = [];
  chatSpeedMode: ChatSpeedMode = 'thinking';
  lastNormalTiming = '';
  activityState: 'idle' | 'thinking' | 'acting' | 'streaming' = 'idle';
  activityText = 'Idle';
  contextSize = DEFAULT_CONTEXT_SIZE;
  maxTokens = DEFAULT_MAX_TOKENS;

  private pendingAutoScroll = false;
  private pendingStreamText = '';
  private pendingStreamFlushTimer: ReturnType<typeof setTimeout> | null = null;
  private lastSyncedSessionId = '';

  constructor(
    public readonly api: LlmApiService,
    private readonly route: ActivatedRoute,
    private readonly chatSession: ChatSessionService,
  ) {}

  ngOnInit(): void {
    this.syncActiveSessionState(true);
    void this.refreshServerConfig();

    this.route.url.subscribe(() => {
      const currentMode =
        this.route.snapshot.routeConfig?.path === 'agent' ? 'agent' : 'chat';
      this.setComposerMode(currentMode);
    });
  }

  ngDoCheck(): void {
    this.syncActiveSessionState();
  }

  ngAfterViewChecked(): void {
    if (!this.pendingAutoScroll) {
      return;
    }

    this.pendingAutoScroll = false;
    const frame = this.chatScrollFrame?.nativeElement;
    if (frame) {
      frame.scrollTop = frame.scrollHeight;
    }
  }

  private syncActiveSessionState(force = false): void {
    const activeId = this.activeSessionId || '';
    if (!force && activeId === this.lastSyncedSessionId) {
      return;
    }

    this.lastSyncedSessionId = activeId;
    this.messages = this.activeSession?.messages ?? [];
    this.prompt = '';
    this.pendingAutoScroll = true;
  }

  get activeSession(): ChatSession | undefined {
    return this.chatSession.activeSession;
  }

  get sessions(): ChatSession[] {
    return this.chatSession.sessions;
  }

  get activeSessionId(): string {
    return this.chatSession.activeSessionId;
  }

  get contextBudgetTokens(): number {
    return Math.max(1, this.contextSize - this.maxTokens);
  }

  get estimatedContextTokens(): number {
    return this.estimateTokens(
      this.composeConversationPrompt(this.messages, ''),
    );
  }

  get remainingContextTokens(): number {
    return Math.max(0, this.contextBudgetTokens - this.estimatedContextTokens);
  }

  get remainingContextPercent(): number {
    return Math.max(
      0,
      Math.min(
        100,
        Math.round(
          (this.remainingContextTokens / this.contextBudgetTokens) * 100,
        ),
      ),
    );
  }

  get sessionLabel(): string {
    return this.activeSession?.title || 'New chat';
  }

  get isAgentMode(): boolean {
    return this.composerMode === 'agent';
  }

  get showAssistantTyping(): boolean {
    return !this.isAgentMode && (this.streamBusy || this.normalBusy);
  }

  get assistantTypingText(): string {
    return this.streamBusy
      ? 'Assistant is streaming...'
      : 'Assistant is thinking...';
  }

  createNewChat(): void {
    this.chatSession.createNewChat();
    this.messages = this.activeSession?.messages ?? [];
    this.prompt = '';
    this.pendingAutoScroll = true;
  }

  switchSession(sessionId: string): void {
    this.chatSession.switchSession(sessionId);
    this.messages = this.activeSession?.messages ?? [];
    this.prompt = '';
    this.pendingAutoScroll = true;
  }

  setComposerMode(mode: ComposerMode): void {
    this.composerMode = mode;
    this.prompt = this.prompt.trim();
  }

  async submitComposer(): Promise<void> {
    if (this.isAgentMode) {
      await this.sendAgent();
      return;
    }

    if (this.chatSpeedMode === 'fast') {
      await this.sendNormal();
      return;
    }

    await this.sendStream();
  }

  async sendNormal(): Promise<void> {
    const prompt = this.prompt.trim();
    if (!prompt || this.normalBusy || this.streamBusy) return;

    this.appendUserMessage(prompt);
    this.prompt = '';
    this.normalBusy = true;
    this.activityState = 'thinking';
    this.activityText = 'Thinking (test mode - full response)';
    this.lastNormalTiming = '';
    this.pendingAutoScroll = true;
    this.saveSessions();

    try {
      const messages = this.buildMessagesPayload();
      const response: NormalResponse = await firstValueFrom(
        this.api.chatNormalFromMessages(messages),
      );
      this.messages.push({
        role: 'assistant',
        content: response.response || '',
      });
      this.touchActiveSession();
      this.lastNormalTiming = `${response.timeElapsedMs} ms (${response.timeElapsedSec.toFixed(2)} s)`;
    } catch (err) {
      this.messages.push({
        role: 'assistant',
        content: `Error: ${this.formatError(err)}`,
      });
      this.touchActiveSession();
    } finally {
      this.normalBusy = false;
      this.activityState = 'idle';
      this.activityText = 'Idle';
      this.saveSessions();
      this.pendingAutoScroll = true;
    }
  }

  async sendStream(): Promise<void> {
    const prompt = this.prompt.trim();
    if (!prompt || this.streamBusy || this.normalBusy) return;

    if (await this.tryHandlePromptFileCreate(prompt)) {
      this.prompt = '';
      this.pendingAutoScroll = true;
      return;
    }

    this.appendUserMessage(prompt);
    const messages = this.buildMessagesPayload();

    this.streamBusy = true;
    this.activityState = 'streaming';
    this.activityText = 'Streaming response...';
    this.pendingStreamText = '';
    this.clearPendingStreamFlush();
    const assistant: ChatMessage = { role: 'assistant', content: '' };
    this.messages.push(assistant);
    this.prompt = '';
    this.pendingAutoScroll = true;
    this.saveSessions();

    try {
      const resp = await fetch(this.api.getChatStreamUrl('thinking'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ messages }),
      });

      if (!resp.ok || !resp.body) {
        throw new Error(`HTTP ${resp.status}`);
      }

      const reader = resp.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { value, done } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });

        let splitIndex = buffer.indexOf('\n\n');
        while (splitIndex !== -1) {
          const event = buffer.slice(0, splitIndex);
          buffer = buffer.slice(splitIndex + 2);

          for (const line of event.split('\n')) {
            if (!line.startsWith('data:')) continue;
            const data = line.startsWith('data: ')
              ? line.slice(6)
              : line.slice(5);

            if (data.trim() === '[DONE]') {
              this.flushPendingStreamText(assistant, true);
              this.streamBusy = false;
              this.activityState = 'idle';
              this.activityText = 'Idle';
              this.touchActiveSession();
              this.saveSessions();
              this.pendingAutoScroll = true;
              return;
            }

            this.activityState = 'acting';
            this.activityText = 'Acting on generated tokens...';
            this.enqueueStreamText(assistant, data.replace(/\\n/g, '\n'));
          }

          splitIndex = buffer.indexOf('\n\n');
        }
      }
    } catch (err) {
      this.flushPendingStreamText(assistant, true);
      assistant.content += `\nError: ${this.formatError(err)}`;
      this.touchActiveSession();
      this.saveSessions();
      this.pendingAutoScroll = true;
    } finally {
      this.flushPendingStreamText(assistant, true);
      this.streamBusy = false;
      this.activityState = 'idle';
      this.activityText = 'Idle';
      this.saveSessions();
    }
  }

  async sendAgent(): Promise<void> {
    const prompt = this.prompt.trim();
    const targetPath = this.agentFilePath.trim();
    if (!prompt || this.agentBusy || this.streamBusy) {
      return;
    }

    if (!targetPath) {
      this.activityState = 'thinking';
      this.activityText = 'Agent mode needs a file path';
      return;
    }

    this.appendUserMessage(`Agent: ${prompt}`);
    this.prompt = '';
    this.pendingAutoScroll = true;

    this.activityState = 'thinking';
    this.activityText = 'Creating file...';
    this.agentBusy = true;

    try {
      const result = await firstValueFrom(
        this.api.createFileFromPrompt(prompt, targetPath),
      );
      this.messages.push({
        role: 'assistant',
        content: result.created
          ? `File created: ${result.createdFilePath ?? targetPath}`
          : result.message,
      });

      this.activityText = 'Running agent workflow...';
      const workflowResult = await firstValueFrom(
        this.api.runAgentPipeline({
          goal: prompt,
          executorPrompt: prompt,
          targetPath,
          testCommand: 'dotnet test llamaCPGGUFllm.sln',
          maxCycles: 3,
        }),
      );

      this.messages.push({
        role: 'assistant',
        content: workflowResult.completed
          ? `Agent completed in ${workflowResult.cyclesUsed} cycle(s).`
          : `Agent stopped after ${workflowResult.cyclesUsed} cycle(s): ${workflowResult.lastErrorSummary ?? 'unknown error'}`,
      });
      this.touchActiveSession();
      this.activityState = 'idle';
      this.activityText = 'Idle';
      this.saveSessions();
      this.pendingAutoScroll = true;
    } catch (err) {
      this.messages.push({
        role: 'assistant',
        content: `Agent error: ${this.formatError(err)}`,
      });
      this.touchActiveSession();
      this.activityState = 'idle';
      this.activityText = 'Idle';
      this.saveSessions();
    } finally {
      this.agentBusy = false;
      this.pendingAutoScroll = true;
    }
  }

  private async tryHandlePromptFileCreate(prompt: string): Promise<boolean> {
    if (!this.isFileCreateIntent(prompt)) {
      return false;
    }

    this.activityState = 'thinking';
    this.activityText = 'Checking file creation rules...';
    this.appendUserMessage(prompt);
    this.saveSessions();

    try {
      const result = await firstValueFrom(
        this.api.createFileFromPrompt(prompt),
      );
      const extra = result.createdFilePath
        ? `\nPath: ${result.createdFilePath}`
        : result.suggestedFolder
          ? `\nSuggested folder: ${result.suggestedFolder}`
          : '';

      this.messages.push({
        role: 'assistant',
        content: `${result.message}${extra}`,
      });
      this.touchActiveSession();
      this.activityState = 'idle';
      this.activityText = 'Idle';
      this.saveSessions();
      return true;
    } catch (err) {
      this.messages.push({
        role: 'assistant',
        content: `Error while creating file: ${this.formatError(err)}`,
      });
      this.touchActiveSession();
      this.activityState = 'idle';
      this.activityText = 'Idle';
      this.saveSessions();
      return true;
    }
  }

  private isFileCreateIntent(prompt: string): boolean {
    const text = prompt.toLowerCase();
    return (
      text.includes('create file') ||
      text.includes('สร้างไฟล์') ||
      text.includes('write file') ||
      text.includes('make file')
    );
  }

  onComposerKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.submitComposer();
    }
  }

  private appendUserMessage(content: string): void {
    this.chatSession.appendUserMessage(content);
    this.messages = this.activeSession?.messages ?? [];
  }

  async searchAndInjectContext(): Promise<void> {
    const query = this.searchQuery.trim();
    if (!query || this.searchBusy) return;

    this.searchBusy = true;
    try {
      const response = await firstValueFrom(this.api.searchWeb(query, 4));
      this.searchResults = response.results ?? [];

      if (this.searchResults.length === 0) return;

      const summary = this.searchResults
        .slice(0, 4)
        .map((r, i) => `${i + 1}. ${r.title}: ${r.snippet} (${r.url})`)
        .join('\n');

      const contextBlock = `[Web search results for "${query}":\n${summary}]\n\n${query}`;
      this.prompt = contextBlock;
      this.showSearch = false;
    } catch (err) {
      // Silently ignore search errors; user can still type manually.
    } finally {
      this.searchBusy = false;
    }
  }

  toggleSearch(): void {
    this.showSearch = !this.showSearch;
    if (!this.showSearch) {
      this.searchResults = [];
    }
  }

  private buildMessagesPayload(): ChatMessagePayload[] {
    const session = this.activeSession;
    if (!session) return [];

    const all = session.messages;
    const budget = Math.max(
      1,
      Math.floor(this.contextBudgetTokens * CONTEXT_SAFETY_MARGIN_RATIO),
    );

    const maxWindow = this.collectRecentWindow(all, RECENT_WINDOW_MAX_TURNS);
    let result = this.trimPayloadToBudget(maxWindow, budget);

    // Try to preserve at least a short recent window when possible.
    if (this.countUserTurns(result) < RECENT_WINDOW_MIN_TURNS) {
      const minWindow = this.collectRecentWindow(all, RECENT_WINDOW_MIN_TURNS);
      result = this.trimPayloadToBudget(minWindow, budget);
    }

    return result;
  }

  private collectRecentWindow(
    messages: ChatMessage[],
    maxUserTurns: number,
  ): ChatMessagePayload[] {
    const result: ChatMessagePayload[] = [];
    let userTurns = 0;

    for (let i = messages.length - 1; i >= 0; i--) {
      const msg = messages[i];
      result.unshift({
        role: msg.role as 'user' | 'assistant',
        content: msg.content,
      });

      if (msg.role === 'user') {
        userTurns += 1;
        if (userTurns >= maxUserTurns) {
          break;
        }
      }
    }

    return result;
  }

  private trimPayloadToBudget(
    payload: ChatMessagePayload[],
    budget: number,
  ): ChatMessagePayload[] {
    const result: ChatMessagePayload[] = [];
    let tokenCount = 0;

    for (let i = payload.length - 1; i >= 0; i--) {
      const msg = payload[i];
      const tokens = this.estimateMessageTokens(msg);
      if (tokenCount + tokens > budget) {
        break;
      }

      tokenCount += tokens;
      result.unshift(msg);
    }

    return result;
  }

  private countUserTurns(payload: ChatMessagePayload[]): number {
    return payload.filter((msg) => msg.role === 'user').length;
  }

  private estimateMessageTokens(message: ChatMessagePayload): number {
    // Include a small per-message overhead for role and separators.
    return this.estimateTokens(message.content) + 8;
  }

  private composeConversationPrompt(
    messages: ChatMessage[],
    _currentPrompt: string,
  ): string {
    const lines: string[] = [];
    const budget = this.contextBudgetTokens;

    for (let index = messages.length - 1; index >= 0; index--) {
      const message = messages[index];
      const chunk = `${message.role === 'user' ? 'User' : 'Assistant'}: ${message.content}`;
      lines.unshift(chunk);

      if (this.estimateTokens(lines.join('\n')) > budget) {
        lines.shift();
        break;
      }
    }

    return lines.join('\n');
  }

  private estimateTokens(text: string): number {
    return Math.max(1, Math.ceil(text.length / 4));
  }

  trackMessage(index: number, message: ChatMessage): string {
    return `${index}:${message.role}`;
  }

  private enqueueStreamText(assistant: ChatMessage, chunk: string): void {
    if (!chunk) {
      return;
    }

    this.pendingStreamText += chunk;
    if (this.pendingStreamFlushTimer !== null) {
      return;
    }

    this.pendingStreamFlushTimer = setTimeout(() => {
      this.pendingStreamFlushTimer = null;
      this.flushPendingStreamText(assistant);
    }, STREAM_FLUSH_INTERVAL_MS);
  }

  private flushPendingStreamText(assistant: ChatMessage, force = false): void {
    if (force) {
      this.clearPendingStreamFlush();
    }

    if (!this.pendingStreamText) {
      return;
    }

    assistant.content += this.pendingStreamText;
    this.pendingStreamText = '';
    this.pendingAutoScroll = true;
  }

  private clearPendingStreamFlush(): void {
    if (this.pendingStreamFlushTimer === null) {
      return;
    }

    clearTimeout(this.pendingStreamFlushTimer);
    this.pendingStreamFlushTimer = null;
  }

  private saveSessions(): void {
    this.chatSession.saveSessions();
  }

  private touchActiveSession(): void {
    this.chatSession.touchActiveSession();
  }

  private async refreshServerConfig(): Promise<void> {
    try {
      const config: ServerConfigResponse = await firstValueFrom(
        this.api.getServerConfig(),
      );
      this.contextSize = config.contextSize || DEFAULT_CONTEXT_SIZE;
      this.maxTokens = config.maxTokens || DEFAULT_MAX_TOKENS;
    } catch {
      this.contextSize = DEFAULT_CONTEXT_SIZE;
      this.maxTokens = DEFAULT_MAX_TOKENS;
    }
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
