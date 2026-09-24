import { Component, computed, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ResourceFieldDto, SECRET_SENTINEL } from '../../../core/models';

/**
 * The edit form for Jira time-tracking resources: the Jira address and token, and how each run's
 * time is booked. The issue itself is picked on the resource's workbench card, where Jira can be
 * searched — the form only says which one the card holds, and never sends it back.
 */
@Component({
  selector: 'app-jira-settings',
  imports: [FormsModule],
  templateUrl: './jira-settings.html',
  styleUrl: './jira-settings.scss',
})
export class JiraSettings {
  readonly values = input.required<Record<string, unknown>>();
  /** The type's field definitions: labels, hints and options come from the API, as for the generic form. */
  readonly fields = input<ResourceFieldDto[]>([]);
  /** False while creating: the card, and so the chooser, exists once the resource is saved. */
  readonly saved = input(false);
  readonly valuesChange = output<Record<string, unknown>>();

  readonly secretSentinel = SECRET_SENTINEL;

  readonly issueKey = computed(() => this.str('issueKey').trim());
  readonly issueSummary = computed(() => this.str('issueSummary').trim());

  field(key: string): ResourceFieldDto | undefined {
    return this.fields().find(f => f.key === key);
  }

  str(key: string): string {
    const value = this.values()[key];
    return typeof value === 'string' ? value : '';
  }

  set(key: string, value: unknown): void {
    this.valuesChange.emit({ ...this.values(), [key]: value });
  }
}
