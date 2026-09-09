import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { EventTopicDto } from '../../core/models';

const TOPIC = /^[a-z0-9-]+(\.[a-z0-9-]+)*$/;
const PATTERN = /^[a-z0-9-]+(\.[a-z0-9-]+)*\.\*$/;

/** Catalog kinds in display order, with the label the group gets. */
const KINDS: { kind: string; label: string }[] = [
  { kind: 'completion', label: 'Agent finishes' },
  { kind: 'raiser', label: 'Raised by a resource' },
  { kind: 'curation', label: 'Memory curation' },
  { kind: 'listener', label: 'Listened for elsewhere' },
  { kind: 'seen', label: 'Seen on the bus' },
];

/**
 * Picks event topics from what the workspace already knows — every agent's completion
 * events, every raiser, every listener, whatever crossed the bus — and lets the user
 * mint a new one with the same validation the API applies. Single (a raiser's topic) or
 * multiple (an agent's listen list); patterns (`prefix.*`) only where listening.
 */
@Component({
  selector: 'app-event-picker',
  imports: [FormsModule],
  templateUrl: './event-picker.html',
  styleUrl: './event-picker.scss',
})
export class EventPicker implements OnInit {
  private readonly api = inject(ApiService);

  /** Newline/comma separated topics (one for single mode). */
  readonly value = input<string>('');
  readonly multiple = input(false);
  readonly allowPatterns = input(false);
  readonly placeholder = input('newsletter.sent');
  readonly valueChange = output<string>();

  protected readonly topics = signal<EventTopicDto[]>([]);
  protected readonly loaded = signal(false);
  protected readonly custom = signal('');

  protected readonly selected = computed<string[]>(() =>
    Array.from(new Set(
      (this.value() ?? '')
        .split(/[\n,]/)
        .map(t => t.trim())
        .filter(t => t.length > 0),
    )),
  );

  /** Catalog entries the user may pick here, grouped in a stable order. */
  protected readonly groups = computed(() => {
    const allowPatterns = this.allowPatterns();
    const topics = this.topics().filter(t => allowPatterns || !t.isPattern);
    return KINDS.map(k => ({ ...k, items: topics.filter(t => t.kind === k.kind) })).filter(g => g.items.length > 0);
  });

  protected readonly customValid = computed(() => this.isValid(this.custom().trim()));
  protected readonly customIsNew = computed(() => {
    const text = this.custom().trim();
    return text.length > 0 && !this.topics().some(t => t.topic === text);
  });

  ngOnInit(): void {
    this.api.getEventTopics().subscribe({
      next: list => {
        this.topics.set(list);
        this.loaded.set(true);
      },
      error: () => this.loaded.set(true), // the custom input still works without the catalog
    });
  }

  protected isSelected(topic: string): boolean {
    return this.selected().includes(topic);
  }

  protected toggle(topic: string): void {
    if (this.multiple()) {
      const next = this.isSelected(topic) ? this.selected().filter(t => t !== topic) : [...this.selected(), topic];
      this.emit(next);
    } else {
      this.emit(this.isSelected(topic) ? [] : [topic]);
    }
  }

  protected remove(topic: string): void {
    this.emit(this.selected().filter(t => t !== topic));
  }

  protected addCustom(): void {
    const text = this.custom().trim();
    if (!this.isValid(text)) return;
    this.custom.set('');
    if (this.isSelected(text)) return;
    this.emit(this.multiple() ? [...this.selected(), text] : [text]);
  }

  /** The API's own rule: dotted lowercase keys; a trailing ".*" only where patterns are allowed. */
  protected isValid(text: string): boolean {
    if (!text) return false;
    return TOPIC.test(text) || (this.allowPatterns() && PATTERN.test(text));
  }

  private emit(list: string[]): void {
    this.valueChange.emit(list.join('\n'));
  }
}
