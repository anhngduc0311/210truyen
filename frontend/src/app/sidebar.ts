import { Component, signal, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, Store, Manga, Page, Comment, ago, compact, message } from './core';
import { Icon } from './ui';

@Component({
  selector: 'app-sidebar',
  imports: [RouterLink, Icon],
  template: `
<aside class="sidebar">
  <!-- 1. READING HISTORY SECTION -->
  <section class="sidebar-section glass-panel">
    <div class="section-title">
      <h2><app-icon name="clock"/> Lịch sử đọc</h2>
      <a routerLink="/lich-su" class="view-all-link">Xem tất cả ›</a>
    </div>

    @if(!store.history().length){
      <div class="empty-history">
        <app-icon name="book"/>
        <p>Chưa có lịch sử đọc truyện</p>
        <small>Khám phá và bắt đầu đọc ngay nào</small>
      </div>
    }

    @for(h of store.history().slice(0, 3); track h.mangaId){
      <a class="history-row" [routerLink]="['/chuong', h.chapterId]">
        <img [src]="h.cover" alt="" loading="lazy" decoding="async" referrerpolicy="no-referrer" (load)="$any($event.target).classList.add('loaded')" (error)="$any($event.target).classList.add('loaded')">
        <div class="history-info">
          <strong>{{h.title}}</strong>
          <small><app-icon name="play" style="width:10px;height:10px;"/> {{h.chapterTitle}}</small>
        </div>
        <app-icon name="chevron" class="chevron-icon"/>
      </a>
    }
  </section>

  <!-- 2. RANKING SECTION -->
  <section class="sidebar-section glass-panel">
    <div class="section-title">
      <h2><app-icon name="trophy"/> Bảng xếp hạng</h2>
    </div>

    <div class="rank-tabs" role="tablist">
      @for(t of tabs; track t.key){
        <button role="tab" [attr.aria-selected]="sort() === t.key" [class.active]="sort() === t.key" (click)="load(t.key)">
          {{t.label}}
        </button>
      }
    </div>

    @if(error()){
      <p class="error">{{error()}} <button (click)="load(sort())">Thử lại</button></p>
    }

    @if(loading()){
      <div class="rank-loading">
        <div class="search-spinner"></div>
        <span>Đang tải bảng xếp hạng…</span>
      </div>
    }

    <div class="rank-list">
      @for(m of ranking(); track m.id; let i = $index){
        <a class="rank-row" 
           [id]="'manga-card-' + m.id" 
           [attr.data-manga-id]="m.id" 
           [routerLink]="['/truyen-tranh', m.id]"
           (click)="savePosition(m.id)">
          <span class="rank-number" [class.top-1]="i === 0" [class.top-2]="i === 1" [class.top-3]="i === 2">{{i + 1}}</span>
          <img [src]="m.cover" [alt]="m.title" loading="lazy" decoding="async" referrerpolicy="no-referrer" (load)="$any($event.target).classList.add('loaded')" (error)="$any($event.target).classList.add('loaded')">
          <div class="rank-info">
            <h3>{{m.title}}</h3>
            <span class="rank-stat">
              <app-icon [name]="sort() === 'rating' ? 'star' : 'heart'"/>
              {{sort() === 'rating' ? (m.rating.toFixed(1) + ' ★') : (compact(m.follows) + ' theo dõi')}}
            </span>
          </div>
        </a>
      }
    </div>
  </section>

  <!-- 3. RECENT COMMENTS -->
  <section class="sidebar-section glass-panel">
    <div class="section-title">
      <h2><app-icon name="comment"/> Bình luận mới</h2>
    </div>

    <div class="sidebar-comments-list">
      @for(c of comments(); track c.id){
        <div class="recent-comment">
          <div class="comment-user-row">
            <span class="avatar mini">{{c.name.slice(0, 1).toUpperCase()}}</span>
            <strong>{{c.name}}</strong>
            <small>{{ago(c.createdAt)}}</small>
          </div>
          <p>{{c.body}}</p>
          <a [routerLink]="['/truyen-tranh', c.mangaId]" (click)="savePosition(c.mangaId)" class="comment-comic-link">
            <app-icon name="book" style="width:11px;height:11px;"/> {{c.mangaTitle}}
          </a>
        </div>
      }
      @if(!comments().length){
        <p class="muted empty-small">Chưa có bình luận nào.</p>
      }
    </div>
  </section>
</aside>`
})
export class Sidebar {
  api = inject(Api);
  store = inject(Store);
  ranking = signal<Manga[]>([]);
  comments = signal<Comment[]>([]);
  sort = signal('hot');
  loading = signal(false);
  error = signal('');
  compact = compact;
  ago = ago;
  private epoch = 0;

  tabs = [
    { key: 'hot', label: '★ Top Hot' },
    { key: 'rating', label: '♥ Yêu thích' },
    { key: 'new', label: '◷ Mới' }
  ];

  constructor() {
    void this.load('hot');
    void this.api.request<Page<Comment>>('/comments').then(r => this.comments.set(r.items.slice(0, 5))).catch(() => {});
  }

  savePosition(id: string) {
    if (typeof window !== 'undefined') {
      sessionStorage.setItem('home_scroll_y', window.scrollY.toString());
      sessionStorage.setItem('home_manga_id', id);
    }
  }

  async load(sort: string) {
    const n = ++this.epoch;
    this.sort.set(sort);
    this.loading.set(true);
    this.error.set('');
    try {
      const r = await this.api.request<Page<Manga>>('/catalog/search?' + this.api.query({
        sort,
        pageSize: 7,
        language: this.store.settings().language
      }), 'GET', undefined, true);
      if (n === this.epoch) this.ranking.set(r.items);
    } catch (e) {
      if (n === this.epoch) this.error.set(message(e));
    } finally {
      if (n === this.epoch) this.loading.set(false);
    }
  }
}
