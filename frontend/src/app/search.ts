import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Api, Store, Manga, Page, message } from './core';
import { Icon, MangaCardComponent, Pagination } from './ui';

@Component({
  selector: 'app-search',
  imports: [FormsModule, Icon, MangaCardComponent, Pagination],
  template: `
<div class="search-page-container">
  <div class="section-title">
    <h1><app-icon name="compass"/> Tìm truyện nâng cao</h1>
    <span class="muted total-count-pill">{{total().toLocaleString('vi')}} bộ truyện</span>
  </div>

  <form class="filter-panel glass-panel" (ngSubmit)="submit()">
    <div class="filter-title-row">
      <div class="search-input-field">
        <app-icon name="search" class="field-icon"/>
        <input type="search" name="q" [(ngModel)]="form.q" placeholder="Tên truyện, tác giả…" aria-label="Tìm theo tên truyện hoặc tác giả" autocomplete="off">
        @if(form.q){
          <button type="button" class="btn-clear-input" aria-label="Xóa từ khóa" (click)="form.q=''">×</button>
        }
      </div>
      <button class="secondary btn-toggle-filter" type="button" [attr.aria-expanded]="expanded" [attr.aria-label]="expanded ? 'Thu gọn bộ lọc' : 'Mở rộng bộ lọc'" (click)="expanded = !expanded">
        <app-icon name="filter"/> 
        <span>Bộ lọc</span>
        <app-icon name="chevronDown" class="filter-chevron" [style.transform]="expanded ? 'rotate(180deg)' : 'none'"/>
      </button>
    </div>

    @if(expanded){
      <div class="filter-grid">
        <label>
          <span>Thể loại truyện</span>
          <select name="genre" [(ngModel)]="form.genre">
            <option value="">Tất cả thể loại</option>
            @for(t of tags(); track t.id){
              <option [value]="t.id">{{t.name}}</option>
            }
          </select>
        </label>

        <label>
          <span>Tình trạng phát hành</span>
          <select name="status" [(ngModel)]="form.status">
            <option value="">Tất cả tình trạng</option>
            <option value="ongoing">Đang tiến hành</option>
            <option value="completed">Đã hoàn thành</option>
            <option value="hiatus">Tạm ngưng</option>
            <option value="cancelled">Đã hủy</option>
          </select>
        </label>

        <label>
          <span>Quốc gia gốc</span>
          <select name="country" [(ngModel)]="form.country">
            <option value="">Tất cả quốc gia</option>
            <option value="ja">Nhật Bản (Manga)</option>
            <option value="ko">Hàn Quốc (Manhwa)</option>
            <option value="zh">Trung Quốc (Manhua)</option>
            <option value="en">Tiếng Anh (Comic)</option>
          </select>
        </label>

        <label>
          <span>Đối tượng độc giả</span>
          <select name="demographic" [(ngModel)]="form.demographic">
            <option value="">Tất cả đối tượng</option>
            <option value="shounen">Shounen (Thiếu niên - Nam)</option>
            <option value="shoujo">Shoujo (Thiếu nữ - Nữ)</option>
            <option value="seinen">Seinen (Trưởng thành - Nam)</option>
            <option value="josei">Josei (Trưởng thành - Nữ)</option>
          </select>
        </label>

        <label>
          <span>Ngôn ngữ bản dịch</span>
          <select name="language" [(ngModel)]="form.language">
            <option value="vi">🇻🇳 Tiếng Việt</option>
            <option value="en">🇬🇧 Tiếng Anh</option>
          </select>
        </label>

        <label>
          <span>Năm phát hành</span>
          <input type="number" min="1950" max="2030" name="year" [(ngModel)]="form.year" placeholder="Ví dụ: 2024">
        </label>
      </div>
    }

    <div class="filter-bottom-bar">
      <label class="sort-field-wrap">
        <span>Sắp xếp theo</span>
        <select name="sort" [(ngModel)]="form.sort">
          <option value="latest">Mới cập nhật chap</option>
          <option value="new">Truyện mới đăng</option>
          <option value="hot">Theo dõi nhiều nhất</option>
          <option value="rating">Đánh giá cao nhất</option>
          <option value="chapters">Nhiều chap nhất</option>
          <option value="title">Bảng chữ cái (A-Z)</option>
        </select>
      </label>

      <div class="filter-action-buttons">
        <button type="button" class="secondary btn-reset-filter" (click)="reset()">Đặt lại</button>
        <button class="primary btn-submit-filter" type="submit"><app-icon name="search"/> Lọc truyện</button>
      </div>
    </div>
  </form>

  <div class="section-title results-title">
    <h2>Kết quả tìm kiếm</h2>
    <span class="muted">{{total().toLocaleString('vi')}} truyện phù hợp</span>
  </div>

  @if(error()){
    <div class="error-state glass-panel">
      <app-icon name="info"/>
      <p>{{error()}}</p>
      <button (click)="load()" class="primary">Thử lại</button>
    </div>
  }

  @if(loading()){
    <div class="loading-panel">
      <div class="search-spinner" style="width:32px;height:32px;margin:0 auto 12px;"></div>
      <p>Đang tìm truyện phù hợp…</p>
    </div>
  }@else{
    <div class="search-grid">
      @for(m of items(); track m.id){
        <app-card [manga]="m"/>
      }
    </div>

    @if(!items().length && !error()){
      <div class="empty-state glass-panel">
        <app-icon name="search"/>
        <h2>Không tìm thấy bộ truyện nào</h2>
        <p>Thử tìm từ khóa ngắn hơn hoặc nới lỏng các tiêu chí bộ lọc.</p>
        <button class="primary" (click)="reset()">Xóa toàn bộ bộ lọc</button>
      </div>
    }

    @if(total() > 24){
      <app-pagination [page]="page()" [total]="total()" [size]="24" [change]="goPage" [hover]="prefetchPage"/>
    }
  }
</div>`
})
export class Search {
  api = inject(Api);
  store = inject(Store);
  router = inject(Router);
  route = inject(ActivatedRoute);

  tags = signal<{ id: string; name: string }[]>([]);
  items = signal<Manga[]>([]);
  total = signal(0);
  page = signal(1);
  loading = signal(true);
  error = signal('');
  expanded = true;
  epoch = 0;

  defaults = () => ({
    q: '',
    genre: '',
    status: '',
    country: '',
    demographic: '',
    language: this.store.settings().language,
    year: '',
    sort: 'latest'
  });
  form = this.defaults();

  constructor() {
    void this.api.request<{ id: string; name: string }[]>('/catalog/tags').then(r => this.tags.set(r)).catch(() => {});
    this.route.queryParamMap.subscribe(p => {
      this.form = this.defaults();
      for (const key of Object.keys(this.form) as (keyof typeof this.form)[]) {
        if (p.has(key)) this.form[key] = p.get(key)!;
      }
      this.page.set(Math.max(1, Number(p.get('page')) || 1));
      void this.load();
    });
  }

  cleanParams(page: number) {
    const qp: Record<string, string | number> = {};
    for (const [key, val] of Object.entries(this.form)) {
      if (val !== '' && val !== undefined && val !== null && key !== 'page') {
        qp[key] = val;
      }
    }
    if (page > 1) {
      qp['page'] = page;
    }
    return qp;
  }

  submit() {
    void this.router.navigate(['/tim-truyen-nang-cao'], { queryParams: this.cleanParams(1) });
  }

  reset() {
    this.api.clearCache('/catalog/search');
    this.form = this.defaults();
    void this.router.navigate(['/tim-truyen-nang-cao']);
  }

  getPath(p: number) {
    return '/catalog/search?' + this.api.query({
      ...this.form,
      page: p,
      pageSize: 24
    });
  }

  prefetchPage = (p: number) => {
    if (p >= 1) {
      this.api.prefetch(this.getPath(p));
    }
  };

  prefetchAdjacent() {
    const p = this.page();
    const max = Math.ceil(this.total() / 24) || 999;
    if (p < max) this.prefetchPage(p + 1);
    if (p > 1) this.prefetchPage(p - 1);
  }

  goPage = (page: number) => {
    this.prefetchPage(page);
    void this.router.navigate([], { relativeTo: this.route, queryParams: this.cleanParams(page) });
    window.scrollTo({ top: 0, behavior: 'smooth' });
  };

  async load() {
    const p = this.page();
    const path = this.getPath(p);
    const n = ++this.epoch;
    this.error.set('');

    const cached = this.api.getCached<Page<Manga>>(path);
    if (cached && cached.items) {
      this.items.set(cached.items);
      this.total.set(cached.total);
      this.loading.set(false);
      this.prefetchAdjacent();
      return;
    }

    this.loading.set(true);
    try {
      const r = await this.api.request<Page<Manga>>(path, 'GET', undefined, true);
      if (n === this.epoch) {
        this.items.set(r.items);
        this.total.set(r.total);
        this.prefetchAdjacent();
      }
    } catch (e) {
      if (n === this.epoch) this.error.set(message(e));
    } finally {
      if (n === this.epoch) this.loading.set(false);
    }
  }
}
