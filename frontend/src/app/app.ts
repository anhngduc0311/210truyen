import { Component, signal, inject, HostListener, ElementRef } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive, Router, NavigationEnd } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Icon } from './ui';
import { Api, Store, Settings, Manga, statuses } from './core';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, FormsModule, Icon],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  store = inject(Store);
  api = inject(Api);
  router = inject(Router);
  el = inject(ElementRef);

  query = '';
  menu = signal(false);
  account = signal(false);
  settingsOpen = signal(false);
  genresOpen = signal(false);
  mobileSearchOpen = signal(false);
  tags = signal<{ id: string; name: string }[]>([]);
  draft: Settings = { ...this.store.settings() };
  reader = signal(false);

  // Search suggestions state
  suggestions = signal<Manga[]>([]);
  suggestionsOpen = signal(false);
  loadingSuggestions = signal(false);
  highlightedIndex = signal(-1);
  private searchTimer?: ReturnType<typeof setTimeout>;
  private searchEpoch = 0;
  statuses = statuses;

  constructor() {
    if (typeof window !== 'undefined') {
      this.reader.set(window.location.pathname.includes('/chuong/'));
      this.applyTheme(this.store.settings().theme);
    }
    this.router.events.subscribe(e => {
      if (e instanceof NavigationEnd) {
        this.menu.set(false);
        this.account.set(false);
        this.genresOpen.set(false);
        this.suggestionsOpen.set(false);
        this.mobileSearchOpen.set(false);
        this.reader.set(e.urlAfterRedirects.includes('/chuong/'));

        const hasSavedHomeScroll = typeof window !== 'undefined' && (sessionStorage.getItem('home_scroll_y') || sessionStorage.getItem('home_manga_id'));
        const isReturningHome = e.urlAfterRedirects === '/' || e.urlAfterRedirects.startsWith('/?');

        if (!hasSavedHomeScroll || !isReturningHome) {
          window.scrollTo(0, 0);
        }
      }
    });
  }

  toggleTheme() {
    const current = this.store.settings().theme;
    const next = current === 'light' ? 'dark' : 'light';
    const newSettings = { ...this.store.settings(), theme: next as 'dark' | 'light' };
    this.store.saveSettings(newSettings);
    this.applyTheme(next);
  }

  private applyTheme(theme: string) {
    if (typeof document !== 'undefined') {
      if (theme === 'light') {
        document.documentElement.setAttribute('data-theme', 'light');
      } else {
        document.documentElement.removeAttribute('data-theme');
      }
    }
  }

  onQueryInput() {
    const q = this.query.trim();
    this.highlightedIndex.set(-1);
    if (!q) {
      this.suggestions.set([]);
      this.suggestionsOpen.set(false);
      this.loadingSuggestions.set(false);
      return;
    }

    this.suggestionsOpen.set(true);
    this.loadingSuggestions.set(true);
    clearTimeout(this.searchTimer);
    const epoch = ++this.searchEpoch;

    this.searchTimer = setTimeout(async () => {
      try {
        const res = await this.api.request<{ items: Manga[]; total: number }>(
          '/catalog/search?' + this.api.query({ q, pageSize: 6 }),
          'GET',
          undefined,
          true
        );
        if (epoch === this.searchEpoch) {
          this.suggestions.set(res.items || []);
          this.loadingSuggestions.set(false);
        }
      } catch {
        if (epoch === this.searchEpoch) {
          this.suggestions.set([]);
          this.loadingSuggestions.set(false);
        }
      }
    }, 220);
  }

  onFocus() {
    if (this.query.trim().length > 0 && (this.suggestions().length > 0 || this.loadingSuggestions())) {
      this.suggestionsOpen.set(true);
    }
  }

  clearSearch() {
    this.query = '';
    this.suggestions.set([]);
    this.suggestionsOpen.set(false);
  }

  onKeydown(event: KeyboardEvent) {
    if (!this.suggestionsOpen()) return;
    const list = this.suggestions();
    const maxIndex = list.length; // 0..list.length-1 for items, list.length for footer "Xem tất cả"

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      const next = this.highlightedIndex() + 1;
      this.highlightedIndex.set(next > maxIndex ? 0 : next);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      const prev = this.highlightedIndex() - 1;
      this.highlightedIndex.set(prev < 0 ? maxIndex : prev);
    } else if (event.key === 'Enter') {
      if (this.highlightedIndex() >= 0 && this.highlightedIndex() < list.length) {
        event.preventDefault();
        this.selectManga(list[this.highlightedIndex()]);
      } else if (this.highlightedIndex() === maxIndex) {
        event.preventDefault();
        this.search();
      }
    } else if (event.key === 'Escape') {
      this.suggestionsOpen.set(false);
      this.mobileSearchOpen.set(false);
    }
  }

  selectManga(m: Manga) {
    this.suggestionsOpen.set(false);
    this.mobileSearchOpen.set(false);
    void this.router.navigate(['/truyen-tranh', m.id]);
  }

  search() {
    this.suggestionsOpen.set(false);
    this.mobileSearchOpen.set(false);
    if (!this.query.trim()) return;
    void this.router.navigate(['/tim-truyen-nang-cao'], { queryParams: { q: this.query.trim() } });
  }

  coverFallback(e: Event) {
    const img = e.target as HTMLImageElement;
    img.onerror = null;
    img.src = '/cover-placeholder.svg';
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent) {
    const wrap = this.el.nativeElement.querySelector('.header-search-wrap');
    if (wrap && !wrap.contains(event.target as Node)) {
      this.suggestionsOpen.set(false);
    }
  }

  openSettings() {
    this.draft = { ...this.store.settings() };
    this.settingsOpen.set(true);
  }

  saveSettings() {
    this.store.saveSettings(this.draft);
    this.applyTheme(this.draft.theme);
    this.settingsOpen.set(false);
  }

  async genres() {
    this.genresOpen.update(v => !v);
    if (!this.tags().length) {
      try {
        this.tags.set(await this.api.request('/catalog/tags'));
      } catch {
        this.store.notify('Không tải được thể loại. Vui lòng thử lại.');
      }
    }
  }

  @HostListener('window:keydown.escape')
  escape() {
    this.settingsOpen.set(false);
    this.menu.set(false);
    this.account.set(false);
    this.genresOpen.set(false);
    this.suggestionsOpen.set(false);
    this.mobileSearchOpen.set(false);
  }
}
