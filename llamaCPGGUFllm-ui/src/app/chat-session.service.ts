import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { LlmApiService } from './llm-api.service';

export type MessageRole = 'user' | 'assistant';

export interface ChatMessage {
  role: MessageRole;
  content: string;
}

export interface ChatSession {
  id: string;
  title: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  messages: ChatMessage[];
}

const SESSION_STORAGE_KEY = 'llm.chat.sessions.v1';
const ACTIVE_SESSION_KEY = 'llm.chat.activeSessionId.v1';

@Injectable({ providedIn: 'root' })
export class ChatSessionService {
  private sessionsState: ChatSession[] = [];
  private activeSessionIdState = '';
  private archiveInFlight = false;
  private archiveQueued = false;

  constructor(private readonly api: LlmApiService) {
    this.loadSessions();
  }

  get sessions(): ChatSession[] {
    return this.sessionsState;
  }

  get activeSessionId(): string {
    return this.activeSessionIdState;
  }

  get activeSession(): ChatSession | undefined {
    return (
      this.sessionsState.find(
        (session) => session.id === this.activeSessionIdState,
      ) ?? this.sessionsState[0]
    );
  }

  createNewChat(): ChatSession {
    const now = new Date().toISOString();
    const id = `chat-${now.replace(/[-:.TZ]/g, '').slice(0, 14)}`;
    const nextSession: ChatSession = {
      id,
      title: 'New chat',
      createdAtUtc: now,
      updatedAtUtc: now,
      messages: [],
    };

    this.sessionsState = [nextSession, ...this.sessionsState];
    this.activeSessionIdState = nextSession.id;
    this.saveSessions();
    return nextSession;
  }

  switchSession(sessionId: string): void {
    const session = this.sessionsState.find((item) => item.id === sessionId);
    if (!session || session.id === this.activeSessionIdState) {
      return;
    }

    this.activeSessionIdState = session.id;
    this.saveSessions();
  }

  appendUserMessage(content: string): void {
    const session = this.activeSession;
    if (!session) {
      return;
    }

    session.messages.push({ role: 'user', content });

    if (session.title === 'New chat' || session.title.startsWith('Chat ')) {
      session.title = this.summarizeTitle(content);
    }

    this.touchActiveSession();
    this.saveSessions();
  }

  touchActiveSession(): void {
    const active = this.activeSession;
    if (!active) {
      return;
    }

    active.updatedAtUtc = new Date().toISOString();
  }

  saveSessions(): void {
    const now = new Date().toISOString();
    const active = this.sessionsState.find(
      (session) => session.id === this.activeSessionIdState,
    );

    if (active) {
      active.updatedAtUtc = now;
    }

    this.safeWriteStorage(
      SESSION_STORAGE_KEY,
      JSON.stringify(this.sessionsState),
    );
    this.safeWriteStorage(ACTIVE_SESSION_KEY, this.activeSessionIdState);
    this.queueArchiveSessionsToFile();
  }

  private queueArchiveSessionsToFile(): void {
    if (this.archiveInFlight) {
      this.archiveQueued = true;
      return;
    }

    this.archiveInFlight = true;
    void firstValueFrom(
      this.api.archiveChatSessions(
        this.sessionsState,
        this.activeSessionIdState,
      ),
    )
      .catch(() => {
        // Keep localStorage as the primary fallback when backend is unavailable.
      })
      .finally(() => {
        this.archiveInFlight = false;
        if (!this.archiveQueued) {
          return;
        }

        this.archiveQueued = false;
        this.queueArchiveSessionsToFile();
      });
  }

  private loadSessions(): void {
    const storedSessions = this.readStoredSessions();

    if (storedSessions.length === 0) {
      const now = new Date().toISOString();
      storedSessions.push({
        id: 'chat-default',
        title: 'New chat',
        createdAtUtc: now,
        updatedAtUtc: now,
        messages: [],
      });
    }

    this.sessionsState = storedSessions;
    const storedActive = this.safeReadStorage(ACTIVE_SESSION_KEY);
    this.activeSessionIdState =
      storedActive &&
      this.sessionsState.some((session) => session.id === storedActive)
        ? storedActive
        : this.sessionsState[0].id;

    this.saveSessions();
  }

  private readStoredSessions(): ChatSession[] {
    const raw = this.safeReadStorage(SESSION_STORAGE_KEY);
    if (!raw) {
      return [];
    }

    try {
      const parsed = JSON.parse(raw) as ChatSession[];
      if (!Array.isArray(parsed)) {
        return [];
      }

      return parsed
        .map((session) => ({
          id: String(session.id || ''),
          title: String(session.title || 'New chat'),
          createdAtUtc: String(
            session.createdAtUtc || new Date().toISOString(),
          ),
          updatedAtUtc: String(
            session.updatedAtUtc || new Date().toISOString(),
          ),
          messages: Array.isArray(session.messages)
            ? session.messages
                .filter(
                  (message) =>
                    message &&
                    (message.role === 'user' || message.role === 'assistant'),
                )
                .map((message) => ({
                  role: message.role,
                  content: String(message.content || ''),
                }))
            : [],
        }))
        .filter((session) => session.id.length > 0);
    } catch {
      return [];
    }
  }

  private summarizeTitle(prompt: string): string {
    const cleaned = prompt.replace(/\s+/g, ' ').trim();
    return cleaned.length > 36 ? `${cleaned.slice(0, 36)}...` : cleaned;
  }

  private safeReadStorage(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  private safeWriteStorage(key: string, value: string): void {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Ignore storage restrictions in browser contexts.
    }
  }
}
