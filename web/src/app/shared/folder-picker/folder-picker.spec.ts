import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { DirectoryListingDto } from '../../core/models';
import { FolderPicker } from './folder-picker';

function listing(path: string, names: string[] = []): DirectoryListingDto {
  return {
    path,
    parentPath: '/home',
    exists: true,
    error: null,
    directories: names.map(name => ({ name, path: `${path}/${name}`, isHidden: name.startsWith('.') })),
    quickLinks: [{ label: 'Home', path: '/home/dev' }],
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

  function create(start: string | null = null) {
    const fixture = TestBed.createComponent(FolderPicker);
    fixture.componentRef.setInput('startPath', start);
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

  it('reports a non-fatal error when the API is unreachable', () => {
    const fixture = create();
    http.expectOne(`${API_BASE}/filesystem/directories`)
      .flush('boom', { status: 500, statusText: 'Server Error' });

    expect(fixture.componentInstance.loadError()).toContain('Couldn’t read that folder');
    expect(fixture.componentInstance.loading()).toBeFalse();
  });
});
