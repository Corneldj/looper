import {
  AfterViewInit,
  Component,
  ElementRef,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { DirectoryListingDto } from '../../core/models';

/**
 * Modal folder browser. The API runs on the same machine as the agents, so it can
 * enumerate real directories and hand back absolute paths — something a browser
 * file input cannot do.
 */
@Component({
  selector: 'app-folder-picker',
  imports: [FormsModule],
  templateUrl: './folder-picker.html',
  styleUrl: './folder-picker.scss',
})
export class FolderPicker implements OnInit, AfterViewInit {
  private readonly api = inject(ApiService);
  private readonly pathBox = viewChild<ElementRef<HTMLInputElement>>('pathBox');

  /** Folder to open at; falls back to the home directory. */
  readonly startPath = input<string | null>(null);
  /** Emits the chosen absolute path, or null when cancelled. */
  readonly picked = output<string | null>();

  readonly listing = signal<DirectoryListingDto | null>(null);
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly showHidden = signal(false);
  /** Editable path box — kept in step with navigation, but typed paths win until Go. */
  readonly pathInput = signal('');

  readonly visibleDirectories = computed(() => {
    const directories = this.listing()?.directories ?? [];
    return this.showHidden() ? directories : directories.filter(d => !d.isHidden);
  });

  readonly hiddenCount = computed(() => (this.listing()?.directories ?? []).filter(d => d.isHidden).length);

  /** Path segments for the breadcrumb, each with the absolute path it navigates to. */
  readonly breadcrumbs = computed(() => {
    const path = this.listing()?.path ?? '';
    const separator = path.includes('\\') ? '\\' : '/';
    const isPosix = path.startsWith('/');
    const parts = path.split(separator).filter(part => part.length > 0);

    const crumbs = isPosix ? [{ label: separator, path: separator }] : [];
    let accumulated = isPosix ? '' : undefined;
    for (const part of parts) {
      accumulated = accumulated === undefined ? part : `${accumulated}${separator}${part}`;
      crumbs.push({ label: part, path: accumulated });
    }
    return crumbs;
  });

  ngOnInit(): void {
    this.navigate(this.startPath());
  }

  /** Take focus so Escape lands here and not on the editor modal underneath. */
  ngAfterViewInit(): void {
    this.pathBox()?.nativeElement.focus();
  }

  /** Escape closes only the picker — the modal that opened it stays put. */
  onEscape(event: Event): void {
    event.stopPropagation();
    this.cancel();
  }

  navigate(path: string | null): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.browseDirectories(path).subscribe({
      next: listing => {
        this.listing.set(listing);
        this.pathInput.set(listing.path);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.loadError.set('Couldn’t read that folder — is the API running?');
      },
    });
  }

  goToTypedPath(): void {
    const typed = this.pathInput().trim();
    if (typed) this.navigate(typed);
  }

  choose(): void {
    const path = this.listing()?.path;
    if (path) this.picked.emit(path);
  }

  cancel(): void {
    this.picked.emit(null);
  }
}
