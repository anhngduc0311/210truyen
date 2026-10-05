import { Component, inject, signal, OnDestroy } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Manga, Page, message } from './core';
import { Icon, MangaCardComponent, Pagination } from './ui';
import { Sidebar } from './sidebar';

@Component({
  selector: 'app-home',
  imports: [RouterLink, Icon, MangaCardComponent, Pagination, Sidebar],
  template: `
<section class="recommendations"
         (mouseenter)="pauseAutoSlide()"
         (mouseleave)="resumeAutoSlide()"
         (touchstart)="pauseAutoSlide()"
         (touchend)="resumeAutoSlide()">
  <div class="section-title">
    <div><span class="eyebrow">ĐÁNG ĐỂ KHÁM PHÁ</span><h1>Truyện Manhwa mới</h1></div>
    <a class="view-all-manhwa-link" routerLink="/tim-truyen-nang-cao" [queryParams]="{country:'ko'}">
      <span class="desktop-text">Xem tất cả</span>
      <span class="mobile-text">Tất cả Manhwa</span>
      <app-icon name="arrowRight"/>
    </a>
  </div>
  
  <div class="featured-carousel-track">
    @for(m of featured(); track m.id; let i = $index){
      <a class="featured-card" 
         [id]="'manga-card-' + m.id" 
         [attr.data-manga-id]="m.id"
         [routerLink]="['/truyen-tranh', m.id]" 
         (click)="savePosition(m.id)"
         (mouseenter)="api.prefetchDetail(m.id)" 
         (touchstart)="api.prefetchDetail(m.id)">
        <img 
          [src]="m.cover" 
          [alt]="m.title" 
          [attr.fetchpriority]="i < 2 ? 'high' : 'auto'" 
          [loading]="i < 2 ? 'eager' : 'lazy'" 
          decoding="async" 
          referrerpolicy="no-referrer" 
          (load)="$any($event.target).classList.add('loaded')" 
          (error)="coverFallback($event)">
        <div class="featured-overlay">
          <span class="featured-badge"><app-icon name="star"/> {{m.rating.toFixed(1)}}</span>
          <h3>{{m.title}}</h3>
          <p class="featured-sub">
            <app-icon name="book" style="width:12px;height:12px;"/>
            {{m.chapters && m.chapters[0] ? m.chapters[0].title : (m.author || 'Đang cập nhật')}}
          </p>
        </div>
      </a>
    }
    @if(featuredLoading()){
      @for(i of [1,2,3,4,5]; track i){
        <div class="skeleton featured-card"></div>
      }
    }
  </div>
  @if(featuredError()){
    <div class="featured-empty"><span>Đề cử đang tạm gián đoạn.</span><button (click)="loadFeatured()">Tải lại <app-icon name="arrowRight"/></button></div>
  }
</section>

<div class="columns">
  <section class="main-content-section">
    <div class="section-title">
      <div><span class="eyebrow">CHƯƠNG MỚI MỖI NGÀY</span><h2>Manga mới cập nhật <span class="update-dot"></span></h2></div>
      <a class="filter-shortcut-btn" routerLink="/tim-truyen-nang-cao" aria-label="Lọc truyện nâng cao">
        <app-icon name="filter"/>
        <span class="btn-text">Bộ lọc</span>
      </a>
    </div>
    
    <div class="list-meta-bar">
      <span>Cập nhật mới nhất → cũ nhất</span>
      <span class="total-badge">{{total().toLocaleString('vi')}} truyện</span>
    </div>

    @if(error()){
      <div class="error-state glass-panel">
        <app-icon name="info"/>
        <h3>Chưa thể tải danh sách truyện</h3>
        <p>{{error()}}</p>
        <button class="primary" (click)="load()">Thử lại</button>
      </div>
    }

    <div class="manga-grid">
      @if(loading()){
        @for(i of skeletons; track i){
          <div class="skeleton skeleton-card"></div>
        }
      }@else{
        @for(m of items(); track m.id){
          <app-card [manga]="m"/>
        }
      }
    </div>

    @if(!loading() && !error()){
      <app-pagination [page]="page()" [total]="total()" [size]="28" [change]="goPage" [hover]="prefetchPage"/>
    }
  </section>

  <app-sidebar/>
</div>
`
})
export class Home implements OnDestroy {
  api = inject(Api);
  route = inject(ActivatedRoute);
  router = inject(Router);

  items = signal<Manga[]>([]);
  pool = signal<Manga[]>([]);
  featured = signal<Manga[]>([]);
  total = signal(0);
  page = signal(1);
  loading = signal(true);
  error = signal('');
  featuredLoading = signal(true);
  featuredError = signal(false);
  skeletons = Array.from({ length: 12 }, (_, i) => i);
  offset = 0;
  epoch = 0;
  private autoSlideTimer?: ReturnType<typeof setInterval>;
  isPaused = false;

  constructor() {
    this.route.queryParamMap.subscribe(p => {
      this.page.set(Math.max(1, Number(p.get('page')) || 1));
      void this.load();
    });
    void this.loadFeatured();
  }

  ngOnDestroy() {
    this.stopAutoSlide();
  }

  savePosition(id?: string) {
    if (typeof window !== 'undefined') {
      sessionStorage.setItem('home_scroll_y', window.scrollY.toString());
      if (id) sessionStorage.setItem('home_manga_id', id);
    }
  }

  restorePosition() {
    if (typeof window === 'undefined') return;
    const savedY = sessionStorage.getItem('home_scroll_y');
    const savedMangaId = sessionStorage.getItem('home_manga_id');
    if (!savedY && !savedMangaId) return;

    setTimeout(() => {
      let restored = false;
      if (savedMangaId) {
        const targetEl = document.getElementById('manga-card-' + savedMangaId) || 
                         document.querySelector(`[data-manga-id="${savedMangaId}"]`);
        if (targetEl) {
          const rect = targetEl.getBoundingClientRect();
          const targetY = window.scrollY + rect.top - 80;
          window.scrollTo({ top: Math.max(0, targetY), behavior: 'instant' });
          targetEl.classList.add('last-viewed-highlight');
          setTimeout(() => targetEl.classList.remove('last-viewed-highlight'), 2500);
          restored = true;
        }
      }

      if (!restored && savedY) {
        const y = Number(savedY);
        if (!isNaN(y) && y > 0) {
          window.scrollTo({ top: y, behavior: 'instant' });
        }
      }

      sessionStorage.removeItem('home_scroll_y');
      sessionStorage.removeItem('home_manga_id');
    }, 60);
  }

  async loadFeatured() {
    this.featuredLoading.set(true);
    this.featuredError.set(false);
    try {
      const r = await this.api.request<Page<Manga>>('/catalog/featured?limit=20', 'GET', undefined, true);
      if (r.items && r.items.length) {
        this.pool.set(r.items);
        this.rotate(0);
        this.startAutoSlide();
      }
    } catch { this.featuredError.set(true); }
    finally { this.featuredLoading.set(false); }
  }

  startAutoSlide() {
    this.stopAutoSlide();
    if (typeof window === 'undefined') return;
    this.autoSlideTimer = setInterval(() => {
      if (!this.isPaused && this.pool().length > 1 && !document.hidden) {
        this.rotate(1);
      }
    }, 3500);
  }

  stopAutoSlide() {
    if (this.autoSlideTimer) {
      clearInterval(this.autoSlideTimer);
      this.autoSlideTimer = undefined;
    }
  }

  pauseAutoSlide() {
    this.isPaused = true;
  }

  resumeAutoSlide() {
    this.isPaused = false;
  }

  manualRotate(step: number) {
    this.rotate(step);
    this.startAutoSlide();
  }

  coverFallback(event: Event) {
    const image = event.target as HTMLImageElement;
    if (!image.src.endsWith('/cover-placeholder.svg')) image.src = '/cover-placeholder.svg';
    image.classList.add('loaded');
  }

  rotate(step: number) {
    const pool = this.pool();
    if (!pool.length) return;
    this.offset = (this.offset + step + pool.length) % pool.length;
    this.featured.set(Array.from({ length: Math.min(6, pool.length) }, (_, i) => pool[(i + this.offset) % pool.length]));
  }

  prefetchPage = (p: number) => {
    if (p >= 1) {
      this.api.prefetch('/catalog/home?page=' + p);
    }
  };

  prefetchAdjacent() {
    const p = this.page();
    const max = Math.ceil(this.total() / 28) || 999;
    if (p < max) this.prefetchPage(p + 1);
    if (p > 1) this.prefetchPage(p - 1);
    if (p + 1 < max) {
      setTimeout(() => {
        if (this.page() === p) this.prefetchPage(p + 2);
      }, 400);
    }
  }

  async load() {
    const p = this.page();
    const path = '/catalog/home?page=' + p;
    const n = ++this.epoch;
    this.error.set('');

    const cached = this.api.getCached<Page<Manga>>(path);
    if (cached && cached.items) {
      this.items.set(cached.items);
      this.total.set(cached.total);
      this.loading.set(false);
      this.prefetchAdjacent();
      this.restorePosition();
      return;
    }

    this.loading.set(true);
    try {
      const r = await this.api.request<Page<Manga>>(path, 'GET', undefined, true);
      if (n === this.epoch) {
        this.items.set(r.items);
        this.total.set(r.total);
        this.prefetchAdjacent();
        this.restorePosition();
      }
    } catch (e) {
      if (n === this.epoch) this.error.set(message(e));
    } finally {
      if (n === this.epoch) this.loading.set(false);
    }
  }

  goPage = (page: number) => {
    this.prefetchPage(page);
    void this.router.navigate(['/'], { queryParams: { page } });
    const targetY = typeof window !== 'undefined' && window.innerWidth <= 768 ? 200 : 350;
    window.scrollTo({ top: targetY, behavior: 'smooth' });
  };
}
