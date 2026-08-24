import { Component } from '@angular/core';
import { ResourcePanel } from './resource-panel/resource-panel';
import { AgentPanel } from './agent-panel/agent-panel';

@Component({
  selector: 'app-workbench',
  imports: [ResourcePanel, AgentPanel],
  templateUrl: './workbench.html',
  styleUrl: './workbench.scss',
})
export class Workbench {}
