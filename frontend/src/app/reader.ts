import { Component, inject, signal, HostListener, OnInit, OnDestroy } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Store, Reader as ReaderData, message, proxyImage } from './core';
import { Icon } from './ui';

export interface ZoomLevel {
  label: string;
  width: number;
}

export interface SpeedLevel {
  label: string;
  speed: number;
}

@Component({
  selector: 'app-reader',
  imports: [FormsModule, RouterLink, Icon],
  template: `
<div class="reader-container" 
     (click)="onReaderClick($event)"
     (touchstart)="onTouchStart($event)"
     (touchend)="onTouchEnd($event)">
  @if(error()){
    <div class="error-state glass-panel">
      <app-icon name="info"/>
      <h2>Chưa thể mở chương</h2>
      <p>{{error()}}</p>
      <button class="primary" (click)="load()">Thử lại</button>
      <a routerLink="/">Về trang chủ</a>
    </div>
  }

  @if(data(); as r){
    <!-- 1. FLOATING TOP HEADER BAR -->
    <header class="reader-header glass-panel" 
            [class.is-hidden]="isHeaderHidden && !isPinned"
            (mouseenter)="isHeaderHovered = true"
            (mouseleave)="isHeaderHovered = false">
      <div class="reader-header-inner">
        <!-- Left: Back Button & Title Meta -->
        <div class="reader-title-box">
          <a [routerLink]="['/truyen-tranh', r.manga.id]" class="btn-back-comic" title="Về trang thông tin truyện" aria-label="Về trang truyện">
            <app-icon name="arrowLeft"/>
          </a>
          <div class="title-meta">
            <h2 class="comic-name">
              <a [routerLink]="['/truyen-tranh', r.manga.id]" title="{{ r.manga.title }}">{{ r.manga.title }}</a>
            </h2>
            <span class="chap-badge">{{ r.chapter.title }}</span>
          </div>
        </div>

        <!-- Center: Chapter Navigation Selector -->
        <div class="reader-nav-controls" (click)="$event.stopPropagation()">
          <button class="btn-nav-chap" 
                  [disabled]="!previous()" 
                  (click)="scrollToTopInstant(); move(-1)" 
                  title="Chương trước (Phím ←)">
            <app-icon name="arrowLeft" style="width: 14px; height: 14px;"/>
            <span>Trước</span>
          </button>

          <div class="chap-select-wrap">
            <select aria-label="Chọn chương" [ngModel]="r.chapter.id" (ngModelChange)="scrollToTopInstant(); go($event)">
              @for(c of r.navigation; track c.id){
                <option [value]="c.id">{{ c.title }}</option>
              }
            </select>
            <app-icon name="chevron" class="select-arrow"/>
          </div>

          <button class="btn-nav-chap btn-nav-next" 
                  [disabled]="!next()" 
                  (click)="scrollToTopInstant(); move(1)" 
                  title="Chương sau (Phím →)">
            <span>Sau</span>
            <app-icon name="arrowRight" style="width: 14px; height: 14px;"/>
          </button>
        </div>

        <!-- Right: Reader Tools -->
        <div class="reader-tools" (click)="$event.stopPropagation()">
          <!-- Reading Mode Toggle -->
          <div class="mode-dropdown-wrap">
            <button class="tool-btn btn-mode-toggle" 
                    (click)="toggleMode()" 
                    [title]="mode() === 'vertical' ? 'Đang cuộn dọc (Click đổi sang từng trang)' : 'Đang từng trang (Click đổi sang cuộn dọc)'">
              <app-icon [name]="mode() === 'vertical' ? 'scroll' : 'book'" style="color: #f5a000; width: 16px; height: 16px;"/>
              <span class="tool-text">{{ mode() === 'vertical' ? 'Cuộn dọc' : 'Từng trang' }}</span>
            </button>
          </div>

          <!-- Zoom Controls Group (Desktop) -->
          <div class="zoom-header-group">
            <button class="tool-btn btn-zoom-action" 
                    (click)="zoomOut()" 
                    [disabled]="isMinZoom" 
                    title="Thu nhỏ (Phím -)" 
                    aria-label="Thu nhỏ">
              <app-icon name="zoomOut" style="width: 14px; height: 14px;"/>
            </button>

            <div class="zoom-dropdown-wrap">
              <button class="tool-btn btn-zoom-label" 
                      (click)="showZoomMenu = !showZoomMenu; showSpeedMenu = false" 
                      title="Độ thu phóng: {{ getZoomLabel() }}">
                <span>{{ getZoomLabel() }}</span>
                <app-icon name="chevron" style="transform: rotate(90deg); width: 10px; height: 10px;"/>
              </button>

              @if(showZoomMenu){
                <div class="zoom-levels-menu header-menu glass-panel">
                  <div class="menu-header">
                    <app-icon name="search" style="width: 14px; height: 14px;"/> Thu phóng trang
                  </div>
                  @for(lvl of zoomLevels; track lvl.width){
                    <button class="menu-item-btn" 
                            [class.active]="zoomWidth() === lvl.width" 
                            (click)="selectZoomWidth(lvl.width)">
                      <span>{{ lvl.label }}</span>
                      @if(zoomWidth() === lvl.width){
                        <app-icon name="check" style="width: 14px; height: 14px; color: #f5a000;"/>
                      }
                    </button>
                  }
                </div>
              }
            </div>

            <button class="tool-btn btn-zoom-action" 
                    (click)="zoomIn()" 
                    [disabled]="isMaxZoom" 
                    title="Phóng to (Phím +)" 
                    aria-label="Phóng to">
              <app-icon name="zoomIn" style="width: 14px; height: 14px;"/>
            </button>
          </div>

          <!-- Auto Scroll Group (Play/Pause + Speed) -->
          @if(mode() === 'vertical'){
            <div class="autoscroll-header-group" [class.is-active]="isAutoScrolling">
              <button class="tool-btn btn-as-action" 
                      [class.active]="isAutoScrolling" 
                      (click)="toggleAutoScroll()" 
                      [title]="isAutoScrolling ? 'Tạm dừng tự cuộn (Phím Space)' : 'Bật tự động cuộn (Phím Space)'">
                <app-icon [name]="isAutoScrolling ? 'pause' : 'play'" style="width: 13px; height: 13px;"/>
              </button>

              <div class="as-speed-wrap">
                <button class="tool-btn btn-as-speed" 
                        (click)="showSpeedMenu = !showSpeedMenu; showZoomMenu = false" 
                        title="Tốc độ tự cuộn: {{ autoScrollSpeed }}x">
                  <span>{{ autoScrollSpeed }}x</span>
                  <app-icon name="chevron" style="transform: rotate(90deg); width: 10px; height: 10px;"/>
                </button>

                @if(showSpeedMenu){
                  <div class="as-speed-menu header-menu glass-panel">
                    <div class="menu-header">
                      <app-icon name="clock" style="width: 14px; height: 14px;"/> Tốc độ cuộn
                    </div>
                    @for(lvl of autoScrollSpeeds; track lvl.speed){
                      <button class="menu-item-btn" 
                              [class.active]="autoScrollSpeed === lvl.speed" 
                              (click)="selectSpeed(lvl.speed)">
                        <span>{{ lvl.label }}</span>
                        @if(autoScrollSpeed === lvl.speed){
                          <app-icon name="check" style="width: 14px; height: 14px; color: #f5a000;"/>
                        }
                      </button>
                    }
                  </div>
                }
              </div>
            </div>
          }

          <!-- Fullscreen Toggle Button -->
          <button class="tool-btn btn-fullscreen" 
                  (click)="toggleFullscreen()" 
                  [title]="isFullscreen ? 'Thoát toàn màn hình (Phím F)' : 'Toàn màn hình (Phím F)'"
                  aria-label="Toàn màn hình">
            <app-icon name="fullscreen" style="width: 15px; height: 15px;"/>
          </button>

          <!-- Report Button -->
          <button class="tool-btn btn-report" 
                  (click)="showReportModal = true" 
                  title="Báo lỗi chương truyện"
                  aria-label="Báo lỗi">
            <app-icon name="flag" style="width: 15px; height: 15px;"/>
          </button>
        </div>
      </div>
    </header>

    <!-- External Source Notice -->
    @if(r.externalUrl){
      <div class="external-source-card glass-panel">
        <p>Chương này được phát hành chính thức trên trang của nhóm dịch.</p>
        <a class="primary" [href]="r.externalUrl" target="_blank" rel="noopener noreferrer">Đọc tại nguồn gốc ↗</a>
      </div>
    }

    <!-- 2. MAIN READING AREA -->
    <!-- Mode 1: Continuous Vertical Scroll -->
    @if(mode() === 'vertical'){
      <div class="reader-pages-vertical" [style.max-width]="zoomWidth() > 0 ? (zoomWidth() + 'px') : '100%'">
        @for(url of pages(); track url; let i = $index){
          <div class="reader-page-item" [id]="'page-' + (i + 1)">
            @if(failed().has(i)){
              <div class="image-error-card">
                <app-icon name="info" style="color: #ef4444; width: 28px; height: 28px; margin-bottom: 8px;"/>
                <p>Không tải được trang {{ i + 1 }} từ máy chủ ảnh.</p>
                <button class="secondary" (click)="retry(i)">Tải lại trang {{ i + 1 }}</button>
              </div>
            }@else{
              <img [src]="pageUrl(url, i)" 
                   [alt]="'Trang ' + (i + 1) + ' — ' + r.chapter.title" 
                   [loading]="i < 3 ? 'eager' : 'lazy'" 
                   decoding="async" 
                   [attr.fetchpriority]="i === 0 ? 'high' : 'auto'" 
                   referrerpolicy="no-referrer" 
                   (load)="onLoaded(i)" 
                   (error)="imageError(i)">
            }
          </div>
        }
      </div>
    }

    <!-- Mode 2: Single Page Reader -->
    @if(mode() === 'single'){
      <div class="reader-single-page-wrap" [style.max-width]="zoomWidth() > 0 ? (zoomWidth() + 'px') : '100%'">
        @if(pages().length > 0){
          <div class="single-page-display">
            <!-- Clickable zones -->
            <div class="click-zone-prev" (click)="prevSinglePage()" title="Trang trước (←)"></div>
            <div class="click-zone-next" (click)="nextSinglePage()" title="Trang sau (→)"></div>

            @if(failed().has(singlePageIndex())){
              <div class="image-error-card">
                <app-icon name="info" style="color: #ef4444; width: 28px; height: 28px; margin-bottom: 8px;"/>
                <p>Không tải được trang {{ singlePageIndex() + 1 }}.</p>
                <button class="secondary" (click)="retry(singlePageIndex())">Thử lại</button>
              </div>
            }@else{
              <img [src]="pageUrl(pages()[singlePageIndex()], singlePageIndex())" 
                   [alt]="'Trang ' + (singlePageIndex() + 1)" 
                   decoding="async"
                   (load)="onLoaded(singlePageIndex())"
                   (error)="imageError(singlePageIndex())">
            }
          </div>

          <!-- Single Page Controls Bar -->
          <div class="single-page-bar glass-panel" (click)="$event.stopPropagation()">
            <button class="btn-page-nav" [disabled]="singlePageIndex() === 0 && !previous()" (click)="prevSinglePage()">
              <app-icon name="arrowLeft" style="width: 14px; height: 14px;"/> Trang trước
            </button>
            <div class="page-counter-select">
              <select [ngModel]="singlePageIndex()" (ngModelChange)="setSinglePage($event)">
                @for(p of pages(); track $index; let idx = $index){
                  <option [value]="idx">Trang {{ idx + 1 }} / {{ pages().length }}</option>
                }
              </select>
            </div>
            <button class="btn-page-nav" [disabled]="singlePageIndex() === pages().length - 1 && !next()" (click)="nextSinglePage()">
              Trang sau <app-icon name="arrowRight" style="width: 14px; height: 14px;"/>
            </button>
          </div>
        }
      </div>
    }

    <!-- 3. BOTTOM CHAPTER NAVIGATION & FOOTER -->
    <div class="reader-bottom-nav glass-panel">
      <h2>Bạn đã đọc hết <span>{{ r.chapter.title }}</span></h2>
      <p class="chapter-info-sub">
        @if(r.manga.title && r.manga.title !== 'Null Meta'){
          {{ r.manga.title }} · 
        }
        {{ pages().length }} trang
      </p>

      <div class="bottom-nav-row">
        <button class="btn-nav-chap" [disabled]="!previous()" (click)="scrollToTopInstant(); move(-1)">
          <app-icon name="arrowLeft" style="width: 14px; height: 14px;"/> <span>Chương trước</span>
        </button>

        <div class="chap-select-wrap">
          <select aria-label="Chọn chương kết thúc" [ngModel]="r.chapter.id" (ngModelChange)="scrollToTopInstant(); go($event)">
            @for(c of r.navigation; track c.id){
              <option [value]="c.id">{{ c.title }}</option>
            }
          </select>
          <app-icon name="chevron" class="select-arrow"/>
        </div>

        <button class="btn-nav-chap btn-nav-next primary" [disabled]="!next()" (click)="scrollToTopInstant(); move(1)">
          <span>Chương sau</span> <app-icon name="arrowRight" style="width: 14px; height: 14px;"/>
        </button>
      </div>

      <div class="bottom-action-links">
        <a [routerLink]="['/truyen-tranh', r.manga.id]" class="bottom-action-btn">
          <app-icon name="book" style="width: 15px; height: 15px;"/> Về trang thông tin truyện
        </a>
        <button class="bottom-action-btn" (click)="showReportModal = true">
          <app-icon name="flag" style="width: 15px; height: 15px;"/> Báo lỗi chương này
        </button>
      </div>
    </div>

    <!-- 4. MOBILE FLOATING QUICK BAR (Visible when controls active) -->
    <div class="reader-mobile-floating-bar glass-panel" [class.is-hidden]="isHeaderHidden && !isPinned" (click)="$event.stopPropagation()">
      <button class="btn-float-action" [disabled]="!previous()" (click)="scrollToTopInstant(); move(-1)" aria-label="Chương trước">
        <app-icon name="arrowLeft"/>
      </button>
      
      <div class="float-chap-info">
        <select aria-label="Chọn chương nhanh" [ngModel]="r.chapter.id" (ngModelChange)="scrollToTopInstant(); go($event)">
          @for(c of r.navigation; track c.id){
            <option [value]="c.id">{{ c.title }}</option>
          }
        </select>
      </div>

      <button class="btn-float-action btn-float-next" [disabled]="!next()" (click)="scrollToTopInstant(); move(1)" aria-label="Chương sau">
        <app-icon name="arrowRight"/>
      </button>
    </div>

    <!-- 5. FLOATING SCROLL TO TOP BUTTON -->
    @if(showScrollTop){
      <button class="btn-scroll-top glass-panel" (click)="scrollToTop()" title="Cuộn lên đầu trang" aria-label="Lên đầu trang">
        <app-icon name="arrowUp" style="width: 18px; height: 18px;"/>
      </button>
    }

    <!-- 6. REPORT MODAL POPUP -->
    @if(showReportModal){
      <div class="report-modal-backdrop" (click)="showReportModal = false">
        <div class="report-modal-dialog glass-panel" (click)="$event.stopPropagation()">
          <div class="modal-header">
            <h3><app-icon name="flag" style="color: #f5a000;"/> Báo lỗi chương truyện</h3>
            <button class="btn-close-modal" (click)="showReportModal = false" aria-label="Đóng">
              <app-icon name="close"/>
            </button>
          </div>

          <div class="modal-body">
            <p class="modal-sub">Truyện: <strong>{{ r.manga.title }}</strong> — {{ r.chapter.title }}</p>
            
            <label class="form-label">Loại sự cố:</label>
            <div class="report-radio-group">
              <label class="radio-option">
                <input type="radio" name="reportType" [(ngModel)]="reportErrorType" value="IMAGE_FAILED">
                <span>Ảnh bị lỗi / không tải được</span>
              </label>
              <label class="radio-option">
                <input type="radio" name="reportType" [(ngModel)]="reportErrorType" value="WRONG_CHAPTER">
                <span>Chương bị sai / trùng lặp nội dung</span>
              </label>
              <label class="radio-option">
                <input type="radio" name="reportType" [(ngModel)]="reportErrorType" value="BAD_TRANSLATION">
                <span>Lỗi dịch thuật / chữ bị mờ</span>
              </label>
              <label class="radio-option">
                <input type="radio" name="reportType" [(ngModel)]="reportErrorType" value="OTHER">
                <span>Vấn đề khác</span>
              </label>
            </div>

            <label class="form-label" style="margin-top: 14px;">Mô tả thêm (tùy chọn):</label>
            <textarea class="report-textarea" 
                      [(ngModel)]="reportDescription" 
                      placeholder="Ghi chú thêm ví dụ: trang số 5 bị mờ hoặc load lâu..."></textarea>
          </div>

          <div class="modal-footer">
            <button class="btn-cancel" (click)="showReportModal = false">Hủy</button>
            <button class="btn-submit primary" (click)="submitReport()">Gửi báo cáo</button>
          </div>
        </div>
      </div>
    }
  }
</div>
`
})
export class Reader implements OnInit, OnDestroy {
  api = inject(Api);
  store = inject(Store);
  route = inject(ActivatedRoute);
  router = inject(Router);
  titleService = inject(Title);

  data = signal<ReaderData | null>(null);
  loading = signal(true);
  error = signal('');
  failed = signal(new Set<number>());
  loaded = signal(new Set<number>());
  fallbackUrls = signal<Map<number, string>>(new Map());

  id = '';
  epoch = 0;

  // Touch Swipe Gesture State
  private touchStartX = 0;
  private touchStartY = 0;

  // Zoom Width State
  zoomWidth = signal<number>(900);
  showZoomMenu = false;
  zoomLevels: ZoomLevel[] = [
    { label: '50%', width: 500 },
    { label: '75%', width: 700 },
    { label: '100%', width: 900 },
    { label: '125%', width: 1150 },
    { label: '150%', width: 1400 },
    { label: '200%', width: 1800 },
    { label: 'Tràn màn', width: 0 }
  ];

  get isMinZoom(): boolean {
    return this.zoomWidth() === 500;
  }

  get isMaxZoom(): boolean {
    return this.zoomWidth() === 0;
  }

  // Reading Mode State
  mode = signal<'vertical' | 'single'>('vertical');
  singlePageIndex = signal<number>(0);

  // Auto-scroll State
  isAutoScrolling = false;
  autoScrollSpeed = 2;
  showSpeedMenu = false;
  autoScrollSpeeds: SpeedLevel[] = [
    { label: '1x (Chậm)', speed: 1 },
    { label: '2x (Vừa)', speed: 2 },
    { label: '3x (Nhanh)', speed: 3 },
    { label: '4x (Rất nhanh)', speed: 4 }
  ];
  private speedPixelsPerSecond: Record<number, number> = {
    1: 45,
    2: 85,
    3: 140,
    4: 220
  };
  private autoScrollAnimFrame: number | null = null;
  private lastFrameTime = 0;
  private scrollSubpixelAccumulator = 0;

  // Smart Header Auto-hide & Zen Mode
  isHeaderHidden = false;
  isHeaderHovered = false;
  isPinned = false;
  isFullscreen = false;
  private lastScrollY = 0;
  showScrollTop = false;

  // Report Modal
  showReportModal = false;
  reportErrorType = 'IMAGE_FAILED';
  reportDescription = '';

  private preloadedUrls = new Set<string>();
  private preloadedNextId = '';
  private onFullscreenChange = () => {
    this.isFullscreen = !!document.fullscreenElement;
  };

  ngOnInit(): void {
    this.loadSavedSettings();
    document.addEventListener('fullscreenchange', this.onFullscreenChange);
    this.route.paramMap.subscribe(p => {
      this.id = p.get('id')!;
      this.singlePageIndex.set(0);
      this.stopAutoScroll();
      this.scrollToTopInstant();
      void this.load();
    });
  }

  ngOnDestroy(): void {
    this.stopAutoScroll();
    document.removeEventListener('fullscreenchange', this.onFullscreenChange);
  }

  loadSavedSettings(): void {
    const savedWidth = localStorage.getItem('td_reader_zoom_width');
    if (savedWidth !== null) {
      const w = parseInt(savedWidth, 10);
      if (!isNaN(w)) this.zoomWidth.set(w);
    } else if (this.store.settings().width) {
      this.zoomWidth.set(this.store.settings().width);
    }

    const savedSpeed = localStorage.getItem('td_reader_autoscroll_speed');
    if (savedSpeed !== null) {
      const s = parseInt(savedSpeed, 10);
      if (!isNaN(s) && s >= 1 && s <= 4) this.autoScrollSpeed = s;
    }

    const savedMode = localStorage.getItem('td_reader_mode') as 'vertical' | 'single';
    if (savedMode === 'vertical' || savedMode === 'single') {
      this.mode.set(savedMode);
    }

    const savedPin = localStorage.getItem('td_reader_pinned');
    this.isPinned = savedPin === 'true';
  }

  async load() {
    const n = ++this.epoch;
    this.data.set(null);
    this.loading.set(true);
    this.error.set('');
    this.failed.set(new Set());
    this.loaded.set(new Set());
    this.fallbackUrls.set(new Map());
    this.preloadedUrls.clear();
    this.preloadedNextId = '';
    this.scrollToTopInstant();

    try {
      const r = await this.api.request<ReaderData>('/chapters/' + this.id, 'GET', undefined, true);
      if (n !== this.epoch) return;
      this.data.set(r);
      if (r && r.manga && r.chapter) {
        this.titleService.setTitle(`${r.manga.title} - ${r.chapter.title} | AkaTruyen`);
      }
      void this.store.record(r).catch(e => this.store.notify(message(e)));
      this.scrollToTopInstant();
      this.preloadUpcoming(0, 4);
    } catch (e) {
      if (n === this.epoch) this.error.set(message(e));
    } finally {
      if (n === this.epoch) this.loading.set(false);
      this.scrollToTopInstant();
    }
  }

  pages(): string[] {
    const r = this.data();
    const list = r ? (this.store.settings().dataSaver && r.dataSaverPages.length ? r.dataSaverPages : r.pages) : [];
    return list.map(proxyImage);
  }

  pageUrl(url: string, i: number): string {
    return this.fallbackUrls().get(i) ?? url;
  }

  onLoaded(i: number): void {
    this.loaded.update(s => new Set(s).add(i));
    if (i === 0 && typeof window !== 'undefined' && window.scrollY > 40) {
      this.scrollToTopInstant();
    }
    this.preloadUpcoming(i + 1, 3);
    const total = this.pages().length;
    if (total > 0 && i >= total - 4) {
      void this.preloadNextChapter();
    }
  }

  preloadUpcoming(startIndex: number, count: number): void {
    const list = this.pages();
    for (let i = startIndex; i < Math.min(list.length, startIndex + count); i++) {
      const url = this.pageUrl(list[i], i);
      if (url && !this.preloadedUrls.has(url)) {
        this.preloadedUrls.add(url);
        const img = new Image();
        img.src = url;
      }
    }
  }

  async preloadNextChapter(): Promise<void> {
    const next = this.next();
    if (!next || this.preloadedNextId === next.id) return;
    this.preloadedNextId = next.id;
    try {
      const r = await this.api.request<ReaderData>('/chapters/' + next.id, 'GET', undefined, true);
      if (!r) return;
      const list = (this.store.settings().dataSaver && r.dataSaverPages?.length ? r.dataSaverPages : r.pages) || [];
      list.slice(0, 3).forEach(url => {
        const p = proxyImage(url);
        if (!this.preloadedUrls.has(p)) {
          this.preloadedUrls.add(p);
          const img = new Image();
          img.src = p;
        }
      });
    } catch { }
  }

  position(): number {
    return this.data()?.navigation.findIndex(c => c.id === this.id) ?? -1;
  }

  previous() {
    const pos = this.position();
    return pos > 0 ? this.data()?.navigation[pos - 1] : undefined;
  }

  next() {
    const pos = this.position();
    const nav = this.data()?.navigation;
    return nav && pos >= 0 && pos < nav.length - 1 ? nav[pos + 1] : undefined;
  }

  go(id: string): void {
    this.scrollToTopInstant();
    void this.router.navigate(['/chuong', id]);
  }

  move(direction: number): void {
    const c = direction < 0 ? this.previous() : this.next();
    if (c) {
      this.scrollToTopInstant();
      this.go(c.id);
    }
  }

  imageError(i: number): void {
    const list = this.pages();
    const current = this.pageUrl(list[i], i);
    if (!current.startsWith('/api/catalog/image-proxy')) {
      const fallback = '/api/catalog/image-proxy?url=' + encodeURIComponent(current);
      this.fallbackUrls.update(m => new Map(m).set(i, fallback));
      return;
    }
    this.failed.update(s => new Set(s).add(i));
  }

  retry(i: number): void {
    this.fallbackUrls.update(m => { const n = new Map(m); n.delete(i); return n; });
    this.failed.update(s => { const n = new Set(s); n.delete(i); return n; });
    this.loaded.update(s => { const n = new Set(s); n.delete(i); return n; });
  }

  // Zoom Controls
  getZoomLabel(): string {
    const current = this.zoomWidth();
    const match = this.zoomLevels.find(l => l.width === current);
    return match ? match.label : `${current}px`;
  }

  selectZoomWidth(width: number): void {
    this.zoomWidth.set(width);
    this.showZoomMenu = false;
    localStorage.setItem('td_reader_zoom_width', width.toString());
    this.store.saveSettings({ ...this.store.settings(), width: width || 900 });
  }

  zoomIn(): void {
    const widths = this.zoomLevels.map(l => l.width);
    const curIdx = widths.indexOf(this.zoomWidth());
    if (curIdx >= 0 && curIdx < widths.length - 1) {
      this.selectZoomWidth(widths[curIdx + 1]);
    } else if (curIdx === -1) {
      this.selectZoomWidth(900);
    }
  }

  zoomOut(): void {
    const widths = this.zoomLevels.map(l => l.width);
    const curIdx = widths.indexOf(this.zoomWidth());
    if (curIdx > 0) {
      this.selectZoomWidth(widths[curIdx - 1]);
    } else if (curIdx === -1) {
      this.selectZoomWidth(700);
    }
  }

  // Reading Modes (Cuộn dọc / Từng trang)
  toggleMode(): void {
    const nextMode = this.mode() === 'vertical' ? 'single' : 'vertical';
    this.mode.set(nextMode);
    localStorage.setItem('td_reader_mode', nextMode);
    if (nextMode === 'single') {
      this.stopAutoScroll();
      window.scrollTo({ top: 0, behavior: 'smooth' });
    }
  }

  prevSinglePage(): void {
    if (this.singlePageIndex() > 0) {
      this.singlePageIndex.update(i => i - 1);
      window.scrollTo({ top: 0, behavior: 'smooth' });
    } else if (this.previous()) {
      this.move(-1);
    }
  }

  nextSinglePage(): void {
    const total = this.pages().length;
    if (this.singlePageIndex() < total - 1) {
      this.singlePageIndex.update(i => i + 1);
      window.scrollTo({ top: 0, behavior: 'smooth' });
    } else if (this.next()) {
      this.move(1);
    }
  }

  setSinglePage(idx: number): void {
    this.singlePageIndex.set(Number(idx));
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  // Touch Swipe Gesture Handling (Single-page mode)
  onTouchStart(e: TouchEvent): void {
    if (e.touches.length === 1) {
      this.touchStartX = e.touches[0].clientX;
      this.touchStartY = e.touches[0].clientY;
    }
  }

  onTouchEnd(e: TouchEvent): void {
    if (this.mode() !== 'single' || e.changedTouches.length === 0) return;
    const deltaX = e.changedTouches[0].clientX - this.touchStartX;
    const deltaY = e.changedTouches[0].clientY - this.touchStartY;

    // If horizontal swipe is significant and more than vertical
    if (Math.abs(deltaX) > 45 && Math.abs(deltaX) > Math.abs(deltaY) * 1.5) {
      if (deltaX > 0) {
        this.prevSinglePage();
      } else {
        this.nextSinglePage();
      }
    }
  }

  // Auto-Scroll Feature
  toggleAutoScroll(): void {
    if (this.isAutoScrolling) {
      this.stopAutoScroll();
    } else {
      this.startAutoScroll();
    }
  }

  startAutoScroll(): void {
    if (this.isAutoScrolling) return;
    this.isAutoScrolling = true;
    this.scrollSubpixelAccumulator = 0;
    this.lastFrameTime = performance.now();

    const scrollLoop = (now: number) => {
      if (!this.isAutoScrolling) return;

      const deltaMs = now - this.lastFrameTime;
      this.lastFrameTime = now;

      if (deltaMs > 0 && deltaMs < 200) {
        const pps = this.speedPixelsPerSecond[this.autoScrollSpeed] || 85;
        const pixelsToScroll = (pps * deltaMs) / 1000;
        this.scrollSubpixelAccumulator += pixelsToScroll;

        const intPixels = Math.floor(this.scrollSubpixelAccumulator);
        if (intPixels > 0) {
          window.scrollBy(0, intPixels);
          this.scrollSubpixelAccumulator -= intPixels;
        }
      }

      // Check if near bottom
      const maxScroll = document.documentElement.scrollHeight - window.innerHeight;
      if (window.scrollY >= maxScroll - 5) {
        this.stopAutoScroll();
        this.store.notify('Đã cuộn đến cuối chương truyện.');
        return;
      }

      this.autoScrollAnimFrame = requestAnimationFrame(scrollLoop);
    };

    this.autoScrollAnimFrame = requestAnimationFrame(scrollLoop);
  }

  stopAutoScroll(): void {
    this.isAutoScrolling = false;
    if (this.autoScrollAnimFrame !== null) {
      cancelAnimationFrame(this.autoScrollAnimFrame);
      this.autoScrollAnimFrame = null;
    }
  }

  selectSpeed(speed: number): void {
    this.autoScrollSpeed = speed;
    this.showSpeedMenu = false;
    localStorage.setItem('td_reader_autoscroll_speed', speed.toString());
  }

  // Fullscreen & Pinning
  toggleFullscreen(): void {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen().catch(() => { });
    } else {
      if (document.exitFullscreen) document.exitFullscreen().catch(() => { });
    }
  }

  togglePin(): void {
    this.isPinned = !this.isPinned;
    if (this.isPinned) this.isHeaderHidden = false;
    localStorage.setItem('td_reader_pinned', this.isPinned.toString());
  }

  scrollToTop(): void {
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  scrollToTopInstant(): void {
    if (typeof window === 'undefined') return;
    try {
      if ('scrollRestoration' in history) {
        history.scrollRestoration = 'manual';
      }
    } catch { }

    const docEl = document.documentElement;
    const prevScrollBehavior = docEl ? docEl.style.scrollBehavior : '';
    if (docEl) docEl.style.scrollBehavior = 'auto';

    const performScroll = () => {
      window.scrollTo(0, 0);
      try {
        window.scrollTo({ top: 0, left: 0, behavior: 'instant' as ScrollBehavior });
      } catch { }
      if (document.documentElement) document.documentElement.scrollTop = 0;
      if (document.body) document.body.scrollTop = 0;
      const el = document.querySelector('.reader-container');
      if (el) el.scrollTop = 0;
    };

    performScroll();
    requestAnimationFrame(() => {
      performScroll();
      if (docEl) docEl.style.scrollBehavior = prevScrollBehavior;
    });

    setTimeout(performScroll, 30);
    setTimeout(performScroll, 100);
    setTimeout(performScroll, 250);
  }

  onReaderClick(e: MouseEvent): void {
    const target = e.target as HTMLElement;
    if (target.closest('button, select, input, a, textarea, .glass-panel, .header-menu, .report-modal-dialog, .reader-mobile-floating-bar')) {
      return;
    }
    this.showZoomMenu = false;
    this.showSpeedMenu = false;
    if (!this.isPinned) {
      this.isHeaderHidden = !this.isHeaderHidden;
    }
  }

  submitReport(): void {
    this.showReportModal = false;
    this.store.notify('Đã gửi báo cáo lỗi chương truyện. Cảm ơn bạn!');
    this.reportDescription = '';
  }

  @HostListener('window:scroll')
  onScroll(): void {
    const currentScrollY = window.scrollY;
    this.showScrollTop = currentScrollY > 400;

    // Smart Header Auto-hide
    if (!this.isPinned && !this.isHeaderHovered) {
      if (currentScrollY > 60 && currentScrollY > this.lastScrollY + 8) {
        this.isHeaderHidden = true;
        this.showZoomMenu = false;
        this.showSpeedMenu = false;
      } else if (currentScrollY < this.lastScrollY - 8 || currentScrollY <= 20) {
        this.isHeaderHidden = false;
      }
    }

    this.lastScrollY = currentScrollY;
  }

  @HostListener('window:keydown', ['$event'])
  key(e: KeyboardEvent): void {
    if (['INPUT', 'SELECT', 'TEXTAREA'].includes((e.target as HTMLElement).tagName)) return;

    if (e.key === 'ArrowLeft' || e.key === 'a' || e.key === 'A') {
      if (this.mode() === 'single') this.prevSinglePage();
      else this.move(-1);
    } else if (e.key === 'ArrowRight' || e.key === 'd' || e.key === 'D') {
      if (this.mode() === 'single') this.nextSinglePage();
      else this.move(1);
    } else if (e.key === '+' || e.key === '=') {
      this.zoomIn();
    } else if (e.key === '-' || e.key === '_') {
      this.zoomOut();
    } else if (e.code === 'Space') {
      e.preventDefault();
      if (this.mode() === 'single') this.nextSinglePage();
      else this.toggleAutoScroll();
    } else if (e.key === 'f' || e.key === 'F') {
      this.toggleFullscreen();
    } else if (e.key === 'Escape') {
      this.showZoomMenu = false;
      this.showSpeedMenu = false;
      this.showReportModal = false;
    }
  }
}
