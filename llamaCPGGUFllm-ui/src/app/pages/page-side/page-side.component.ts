import { CommonModule } from '@angular/common';
import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { ChatSession, ChatSessionService } from '../../chat-session.service';

@Component({
  selector: 'app-page-side',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive],
  templateUrl: './page-side.component.html',
  styleUrl: './page-side.component.scss',
})
export class PageSideComponent {
  constructor(private readonly chatSession: ChatSessionService) {}

  get sessions(): ChatSession[] {
    return this.chatSession.sessions;
  }

  get activeSessionId(): string {
    return this.chatSession.activeSessionId;
  }

  createNewChat(): void {
    this.chatSession.createNewChat();
  }

  switchSession(sessionId: string): void {
    this.chatSession.switchSession(sessionId);
  }

  trackSession(_: number, session: ChatSession): string {
    return session.id;
  }
}
