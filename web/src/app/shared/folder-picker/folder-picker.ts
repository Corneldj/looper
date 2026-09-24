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
import { DirectoryListingDto, FileEntryDto } from '../../core/models';

/**
 * Modal folder browser. The API runs on the same machine as the agents, so it can
 * enumerate real directories and hand back absolute paths — something a browser
 * file input cannot do. In file mode it lists files as well and picks one of them.
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

  /** Folder to open at; falls back to the home directory. In file mode a file path opens its folder, selected. */
  readonly startPath = input<string | null>(null);
  /** 'folder' picks the folder being viewed; 'file' lists files too and picks one of them. */
  readonly mode = input<'folder' | 'file'>('folder');
  /** Emits the chosen absolute path, or null when cancelled. */
  readonly picked = output<string | null>();

  readonly listing = signal<DirectoryListingDto | null>(null);
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly showHidden = signal(false);
  /** Editable path box — kept in step with navigation, but typed paths win until Go. */
  readonly pathInput = signal('');
  /** File mode only: the absolute path of the highlighted file. */
  readonly selectedFile = signal<string | null>(null);

  readonly isFileMode = computed(() => this.mode() === 'file');

  readonly visibleDirectories = computed(() => {
    const directories = this.listing()?.directories ?? [];
    return this.showHidden() ? directories : directories.filter(d => !d.isHidden);
  });

  readonly visibleFiles = computed(() => {
    const files = this.listing()?.files ?? [];
    return this.showHidden() ? files : files.filter(f => !f.isHidden);
  });

  readonly hiddenCount = computed(
    () =>
      (this.listing()?.directories ?? []).filter(d => d.isHidden).length +
      (this.listing()?.files ?? []).filter(f => f.isHidden).length,
  );

  readonly selectedFileName = computed(() => {
    const path = this.selectedFile();
    return path ? path.split(/[\\/]/).pop() ?? path : null;
  });

  /** A folder can always be chosen once it exists; a file has to be picked first. */
  readonly canChoose = computed(() =>
    this.isFileMode() ? this.selectedFile() !== null : (this.listing()?.exists ?? false),
  );

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
    this.api.browseDirectories(path, this.isFileMode()).subscribe({
      next: listing => {
        this.listing.set(listing);
        this.pathInput.set(listing.path);
        // The API lists a file path's folder; keep the file the user meant selected.
        this.selectedFile.set(this.isFileMode() ? this.matchFile(path, listing) : null);
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

  selectFile(file: FileEntryDto): void {
    this.selectedFile.set(file.path);
    this.pathInput.set(file.path);
  }

  /** Double-clicking a file is "select and use it" in one go. */
  chooseFile(file: FileEntryDto): void {
    this.selectFile(file);
    this.choose();
  }

  choose(): void {
    if (this.isFileMode()) {
      const file = this.selectedFile();
      if (file) this.picked.emit(file);
      return;
    }
    const path = this.listing()?.path;
    if (path) this.picked.emit(path);
  }

  cancel(): void {
    this.picked.emit(null);
  }

  formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(bytes < 10 * 1024 ? 1 : 0)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  private matchFile(requested: string | null, listing: DirectoryListingDto): string | null {
    if (!requested) return null;
    const wanted = requested.trim().replace(/[\\/]+$/, '').toLowerCase();
    return listing.files.find(f => f.path.toLowerCase() === wanted)?.path ?? null;
  }
}
