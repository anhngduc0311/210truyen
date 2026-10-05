import { Component, input, signal, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, Manga, compact, ago } from './core';

@Component({
  selector: 'app-icon',
  template: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path [attr.d]="paths[name()] || paths['book']"/></svg>`,
  styles: [`:host{display:inline-flex;width:20px;height:20px;flex-shrink:0;vertical-align:middle}svg{width:100%;height:100%}`]
})
export class Icon {
  name = input('book');
  paths: Record<string, string> = {
    search: 'm21 21-5-5 M19 10.5a8.5 8.5 0 1 1-17 0a8.5 8.5 0 0 1 17 0',
    home: 'm3 10 9-7 9 7v11h-6v-7H9v7H3z',
    heart: 'M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1.1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21l8.8-8.6a5.5 5.5 0 0 0 0-7.8Z',
    clock: 'M12 8v5l3 2 M22 12a10 10 0 1 1-20 0a10 10 0 0 1 20 0',
    star: 'm12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2L12 17.3l-5.6 2.9 1.1-6.2L3 9.6l6.2-.9Z',
    fire: 'M12 2s2 6-2 9c0-3-3-4-3-4s-5 6-2 11c3 6 12 5 14-1s-2-10-3-11c1 5-2 6-2 6s2-6-2-10Z',
    trophy: 'M8 3h8v8a4 4 0 0 1-8 0V3Z M8 5H3v4a4 4 0 0 0 5 4m8-8h5v4a4 4 0 0 1-5 4M12 15v6m-5 0h10',
    book: 'M3 4h6l3 2 3-2h6v16h-6l-3 2-3-2H3V4Zm9 2v16',
    bookmark: 'M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z',
    library: 'M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1-2.5-2.5Z M4 19.5a2.5 2.5 0 0 0 2.5 2.5H20 M6 6h10 M6 10h10',
    compass: 'm16.2 7.8-2 6.4-6.4 2 2-6.4 6.4-2Z M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0',
    comment: 'M21 3H3v14h5l4 4 4-4h5V3Z',
    user: 'M16 6a4 4 0 1 1-8 0a4 4 0 0 1 8 0M4 22v-3a8 8 0 0 1 16 0v3',
    settings: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z',
    chevron: 'm9 5 7 7-7 7',
    chevronDown: 'm6 9 6 6 6-6',
    filter: 'M3 4h18l-7 8v8l-4-2v-6Z',
    menu: 'M3 6h18M3 12h18M3 18h18',
    close: 'm6 6 12 12M6 18 18 6',
    check: 'm4 12 5 5L20 6',
    arrow: 'M19 12H5m6-6-6 6 6 6',
    arrowLeft: 'M19 12H5m6-6-6 6 6 6',
    arrowRight: 'M5 12h14m-6-6 6 6-6 6',
    arrowUp: 'M12 19V5m-6 6 6-6 6 6',
    sun: 'M12 2v2m0 16v2M2 12h2m16 0h2M5 5l2 2m10 10 2 2M5 19l2-2M17 7l2-2M16 12a4 4 0 1 1-8 0a4 4 0 0 1 8 0',
    moon: 'M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z',
    info: 'M12 11v6m0-10v.1M22 12a10 10 0 1 1-20 0a10 10 0 0 1 20 0',
    play: 'M5 3l14 9-14 9V3z',
    pause: 'M6 4h4v16H6zm8 0h4v16h-4z',
    zoomIn: 'm21 21-5-5 M19 10.5a8.5 8.5 0 1 1-17 0a8.5 8.5 0 0 1 17 0 M10.5 7v7 M7 10.5h7',
    zoomOut: 'm21 21-5-5 M19 10.5a8.5 8.5 0 1 1-17 0a8.5 8.5 0 0 1 17 0 M7 10.5h7',
    fullscreen: 'M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3m0 18h3a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2h3',
    flag: 'M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1zm0 7v-7',
    scroll: 'M4 6h16M4 12h16M4 18h16',
    sparkles: 'm12 3-1.9 5.8a2 2 0 0 1-1.3 1.3L3 12l5.8 1.9a2 2 0 0 1 1.3 1.3L12 21l1.9-5.8a2 2 0 0 1 1.3-1.3L21 12l-5.8-1.9a2 2 0 0 1-1.3-1.3L12 3Z',
    tag: 'M20.6 13.4 12 4.8A2 2 0 0 0 10.6 4.2H4.2A2.2 2.2 0 0 0 2 6.4v6.4a2 2 0 0 0 .6 1.4l8.6 8.6a2 2 0 0 0 2.8 0l6.6-6.6a2 2 0 0 0 0-2.8Z M7 9a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3',
    logout: 'M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4 M16 17l5-5-5-5 M21 12H9',
    dashboard: 'M3 3h7v9H3zm11 0h7v5h-7zm0 9h7v9h-7zM3 16h7v5H3z',
    layers: 'M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5',
    users: 'M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2 M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8 M23 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75',
    shield: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z',
    coins: 'M12 1v22M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6',
    edit: 'M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7 M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z',
    trash: 'M3 6h18 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2 M10 11v6 M14 11v6',
    plus: 'M12 5v14M5 12h14',
    alertCircle: 'M12 8v4m0 4h.01M22 12A10 10 0 1 1 2 12a10 10 0 0 1 20 0z',
    eye: 'M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
    eyeOff: 'M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24 M1 1l22 22',
    refresh: 'M23 4v6h-6 M1 20v-6h6 M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15',
    fileText: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z M14 2v6h6 M16 13H8 M16 17H8 M10 9H8',
    image: 'M19 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2z M8.5 10a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3z M21 15l-5-5L5 21',
    lock: 'M19 11H5a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7a2 2 0 0 0-2-2z M7 11V7a5 5 0 0 1 10 0v4',
    unlock: 'M19 11H5a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7a2 2 0 0 0-2-2z M7 11V7a5 5 0 0 1 9.9-1',
    download: 'M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3',
    fileZip: 'M4 4v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6H6a2 2 0 0 0-2 2z M14 2v6h6 M10 12h4 M10 16h4',
    archive: 'M21 8v13H3V8 M1 3h22v5H1z M10 12h4'
  };
}

@Component({
  selector: 'app-card',
  imports: [RouterLink, Icon],
  template: `
<article class="manga-card" [id]="'manga-card-' + manga().id" [attr.data-manga-id]="manga().id">
  <a class="cover-link" 
     [routerLink]="['/truyen-tranh', manga().id]" 
     (click)="savePosition()"
     (mouseenter)="prefetch()" 
     (touchstart)="prefetch()" 
     [attr.aria-label]="manga().title">
    <img [src]="manga().cover" [alt]="manga().title" loading="lazy" decoding="async" referrerpolicy="no-referrer" (load)="loaded.set(true)" [class.loaded]="loaded()" (error)="fallback($event)">
    @if(manga().contentRating === 'erotica' || manga().contentRating === 'pornographic'){
      <span class="age-label">18+</span>
    }
  </a>
  <div class="manga-card-info">
    <div class="card-stats">
      <span><app-icon name="star"/> {{manga().rating.toFixed(1)}}</span>
      <span><app-icon name="heart"/> {{compact(manga().follows)}}</span>
    </div>
    <h3><a [routerLink]="['/truyen-tranh', manga().id]" (click)="savePosition()" [title]="manga().title">{{manga().title}}</a></h3>
  </div>
  <div class="mini-chapters">
    @for(c of manga().chapters.slice(0, 3); track c.id){
      <div class="mini-chap-item">
        <a [routerLink]="['/chuong', c.id]" [title]="c.title">{{c.title}}</a>
        <time>{{ago(c.publishedAt)}}</time>
      </div>
    }
    @if(!manga().chapters.length){
      <div class="mini-chap-item">
        <span class="muted-author">{{manga().author || 'Đang cập nhật'}}</span>
      </div>
    }
  </div>
</article>`
})
export class MangaCardComponent {
  api = inject(Api);
  manga = input.required<Manga>();
  loaded = signal(false);
  compact = compact;
  ago = ago;
  private prefetched = false;

  savePosition() {
    if (typeof window !== 'undefined') {
      sessionStorage.setItem('home_scroll_y', window.scrollY.toString());
      sessionStorage.setItem('home_manga_id', this.manga().id);
    }
  }

  prefetch() {
    if (this.prefetched) return;
    this.prefetched = true;
    this.api.prefetchDetail(this.manga().id);
  }

  fallback(e: Event) {
    const img = e.target as HTMLImageElement;
    img.onerror = null;
    img.src = '/cover-placeholder.svg';
    this.loaded.set(true);
  }
}

@Component({
  selector: 'app-pagination',
  imports: [Icon],
  template: `
<nav class="pagination" aria-label="Phân trang">
  <button class="pag-btn" 
          [disabled]="page() <= 1" 
          (click)="change()(page() - 1)" 
          (mouseenter)="onHover(page() - 1)" 
          (touchstart)="onHover(page() - 1)" 
          aria-label="Trang trước">
    <app-icon name="arrowLeft"/>
  </button>
  @for(p of numbers(); track p){
    <button class="pag-btn" 
            [class.active]="page() === p" 
            [attr.aria-current]="page() === p ? 'page' : null" 
            (click)="change()(p)" 
            (mouseenter)="onHover(p)" 
            (touchstart)="onHover(p)">{{p}}</button>
  }
  @if(totalPages() > page() + 2){
    <span class="pag-ellipsis">…</span>
    <button class="pag-btn" 
            (click)="change()(totalPages())" 
            (mouseenter)="onHover(totalPages())" 
            (touchstart)="onHover(totalPages())">{{totalPages()}}</button>
  }
  <button class="pag-btn" 
          [disabled]="page() >= totalPages()" 
          (click)="change()(page() + 1)" 
          (mouseenter)="onHover(page() + 1)" 
          (touchstart)="onHover(page() + 1)" 
          aria-label="Trang tiếp">
    <app-icon name="arrowRight"/>
  </button>
</nav>`
})
export class Pagination {
  page = input(1);
  total = input(0);
  size = input(28);
  change = input.required<(n: number) => void>();
  hover = input<((n: number) => void) | undefined>();

  onHover(n: number) {
    const fn = this.hover();
    if (fn && n >= 1 && n <= this.totalPages() && n !== this.page()) {
      fn(n);
    }
  }

  totalPages() {
    return Math.max(1, Math.ceil(this.total() / this.size()));
  }

  numbers() {
    const start = Math.max(1, this.page() - 2);
    return Array.from({ length: Math.max(0, Math.min(start + 4, this.totalPages()) - start + 1) }, (_, i) => start + i);
  }
}
