import { CommonModule } from '@angular/common';
import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { LlmApiService } from './llm-api.service';

@Component({
  selector: 'app-root',
  imports: [CommonModule, RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  title = 'Llama Chat Console';

  constructor(public readonly api: LlmApiService) {}

  async ngOnInit(): Promise<void> {
    try {
      await this.api.refreshAppState();
    } catch {
      // First-load status can fail when server is still down; pages handle details.
    }
  }
}
