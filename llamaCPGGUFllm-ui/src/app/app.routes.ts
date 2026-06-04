import { Routes } from '@angular/router';
import { ChatPageComponent } from './pages/chat-page/chat-page.component';
import { SettingsPageComponent } from './pages/settings-page/settings-page.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'chat' },
  { path: 'chat', component: ChatPageComponent },
  { path: 'settings', component: SettingsPageComponent },
  { path: 'agent', component: ChatPageComponent },
  { path: '**', redirectTo: 'chat' },
];
