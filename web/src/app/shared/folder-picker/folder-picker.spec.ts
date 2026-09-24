import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { DirectoryListingDto } from '../../core/models';
import { FolderPicker } from './folder-picker';

function listing(path: string, names: string[] = [], files: string[] = []): DirectoryListingDto {
  return {
    path,
    parentPath: '/home',
    exists: true,
    error: null,
    directories: names.map(name => ({ name, path: `${path}/${name}`, isHidden: name.startsWith('.') })),
    quickLinks: [{ label: 'Home', path: '/home/dev' }],
    files: files.map(name => ({ name, path: `${path}/${name}`, isHidden: name.startsWith('.'), sizeBytes: 12 })),
  };
}

describe('FolderPicker', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FolderPicker],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create(start: string | null = null, mode: 'folder' | 'file' = 'folder') {
    const fixture = TestBed.createComponent(FolderPicker);
    fixture.componentRef.setInput('startPath', start);
    fixture.componentRef.setInput('mode', mode);
    fixture.detectChanges();
    return fixture;
  }

  it('loads the home folder when no start path is given', () => {
    const fixture = create();
    const request = http.expectOne(`${API_BASE}/filesystem/directories`);
    expect(request.request.method).toBe('GET');
    request.flush(listing('/home/dev', ['projects']));

    expect(fixture.componentInstance.listing()?.path).toBe('/home/dev');
    expect(fixture.componentInstance.pathInput()).toBe('/home/dev');
  });

  it('requests the start path when one is supplied', () => {
    create('/srv/code');
    const request = http.expectOne(r => r.url === `${API_BASE}/filesystem/directories`);
    expect(request.request.params.get('path')).toBe('/srv/code');
    expect(request.request.params.has('includeFiles')).withContext('folder mode asks for folders only').toBeFalse();
    request.flush(listing('/srv/code'));
  });

  it('builds breadcrumbs that navigate to each ancestor', () => {
    const fixture = create();
    http.expectOne(`${API_BASE}/filesystem/directories`).flush(listing('/home/dev/projects'));

    expect(fixture.componentInstance.breadcrumbs()).toEqual([
      { label: '/', path: '/' },
      { label: 'home', path: '/home' },
      { label: 'dev', path: '/home/dev' },
      { label: 'projects', path: '/home/dev/projects' },
    ]);
  });

  it('hides dot-folders until asked, and counts them', () => {
    const fixture = create();
    http.expectOne(`${API_BASE}/filesystem/directories`)
      .flush(listing('/home/dev', ['.cache', 'apps', '.config']));
    const picker = fixture.componentInstance;

    expect(picker.visibleDirectories().map(d => d.name)).toEqual(['apps']);
    expect(picker.hiddenCount()).toBe(2);

    picker.showHidden.set(true);
    expect(picker.visibleDirectories().length).toBe(3);
  });

  it('emits the current folder on choose and null on cancel', () => {
    const fixture = create();
    http.expectOne(`${API_BASE}/filesystem/directories`).flush(listing('/home/dev/projects'));
    const picker = fixture.componentInstance;

    const emitted: (string | null)[] = [];
    picker.picked.subscribe(value => emitted.push(value));

    picker.choose();
    picker.cancel();

    expect(emitted).toEqual(['/home/dev/projects', null]);
  });

  it('in file mode asks for files, preselects a typed file path, and emits the chosen file', () => {
    const fixture = create('/home/dev/brief.md', 'file');
    const request = http.expectOne(r => r.url === `${API_BASE}/filesystem/directories`);
    expect(request.request.params.get('includeFiles')).toBe('true');
    expect(request.request.params.get('path')).toBe('/home/dev/brief.md');
    request.flush(listing('/home/dev', ['projects'], ['brief.md', '.env', 'notes.txt']));
    const picker = fixture.componentInstance;

    expect(picker.visibleFiles().map(f => f.name)).toEqual(['brief.md', 'notes.txt']); // dot-files hide like dot-folders
    expect(picker.hiddenCount()).toBe(1);
    expect(picker.selectedFile()).withContext('the API listed the file’s folder; the file stays selected').toBe('/home/dev/brief.md');
    expect(picker.canChoose()).toBeTrue();

    const emitted: (string | null)[] = [];
    picker.picked.subscribe(value => emitted.push(value));
    picker.selectFile({ name: 'notes.txt', path: '/home/dev/notes.txt', isHidden: false, sizeBytes: 12 });
    expect(picker.pathInput()).toBe('/home/dev/notes.txt');
    picker.choose();

    expect(emitted).toEqual(['/home/dev/notes.txt']);
  });

  it('in file mode nothing can be chosen until a file is selected', () => {
    const fixture = create('/home/dev', 'file');
    http.expectOne(r => r.url === `${API_BASE}/filesystem/directories`).flush(listing('/home/dev', ['projects'], ['a.md']));
    const picker = fixture.componentInstance;

    expect(picker.selectedFile()).toBeNull();
    expect(picker.canChoose()).toBeFalse();

    const emitted: (string | null)[] = [];
    picker.picked.subscribe(value => emitted.push(value));
    picker.choose();
    expect(emitted).withContext('choose with nothing selected is a no-op').toEqual([]);

    picker.chooseFile({ name: 'a.md', path: '/home/dev/a.md', isHidden: false, sizeBytes: 3 });
    expect(emitted).toEqual(['/home/dev/a.md']);
  });

  it('reports a non-fatal error when the API is unreachable', () => {
    const fixture = create();
    http.expectOne(`${API_BASE}/filesystem/directories`)
      .flush('boom', { status: 500, statusText: 'Server Error' });

    expect(fixture.componentInstance.loadError()).toContain('Couldn’t read that folder');
    expect(fixture.componentInstance.loading()).toBeFalse();
  });
});
