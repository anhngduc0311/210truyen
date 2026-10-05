import { Component, inject, signal } from '@angular/core';
import { Title, Meta } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, Store, Manga, Chapter, Comment, Page, Reader as ReaderData, message, statuses, compact, ago, proxyImage, cleanDescription } from './core';
import { Icon, Pagination } from './ui';
import { Sidebar } from './sidebar';

@Component({
  selector: 'app-detail',
  imports: [RouterLink, FormsModule, Icon, Pagination, Sidebar],
  template: `
<div class="breadcrumb">
  <button type="button" class="btn-breadcrumb-back" (click)="goBack($event)" aria-label="Quay lại" title="Quay lại trang trước">
    <app-icon name="arrowLeft"/>
    <span>Quay lại</span>
  </button>
  <span class="breadcrumb-sep">›</span>
  <a routerLink="/" (click)="goBack($event)">Trang chủ</a>
  <span class="breadcrumb-sep">›</span>
  <span class="current-crumb">{{manga()?.title || 'Chi tiết truyện'}}</span>
</div>

@if(error()){
  <div class="error-state glass-panel">
    <app-icon name="info"/>
    <h3>Chưa thể tải thông tin truyện</h3>
    <p>{{error()}}</p>
    <button class="primary" (click)="load()">Thử lại</button>
  </div>
}

@if(manga(); as m){
  <div class="columns">
    <div class="detail-main-column">
      <!-- 1. DETAIL HERO SECTION -->
      <section class="detail-hero glass-panel">
        <div class="hero-backdrop" [style.background-image]="'url(' + m.cover + ')'"></div>
        <div class="hero-overlay-content">
          <div class="detail-cover">
            <img [src]="m.cover" [alt]="m.title" fetchpriority="high" decoding="async" referrerpolicy="no-referrer" (load)="$any($event.target).classList.add('loaded')" (error)="$any($event.target).classList.add('loaded')">
            @if(m.contentRating === 'erotica' || m.contentRating === 'pornographic'){
              <span class="age-badge-detail">18+</span>
            }
          </div>

          <div class="detail-info">
            <h1 class="detail-title">{{m.title}}</h1>
            @if(m.alternativeTitle && m.alternativeTitle.trim().toLowerCase() !== m.title.trim().toLowerCase()){
              <p class="alternative">{{m.alternativeTitle}}</p>
            }

            <div class="detail-stats">
              <span class="stat-pill star"><app-icon name="star"/> {{m.rating.toFixed(1)}}</span>
              <span class="stat-pill heart"><app-icon name="heart"/> {{compact(m.follows)}}</span>
              <span class="stat-pill comment"><app-icon name="comment"/> {{commentsTotal()}}</span>
            </div>

            <!-- Primary CTAs on Mobile / Desktop -->
            <div class="detail-hero-actions">
              @if(resume()){
                <a class="primary btn-read-main" (mouseenter)="preloadFirstChapter()" [routerLink]="['/chuong', resume()?.chapterId]">
                  <app-icon name="play"/> Đọc tiếp {{resume()?.chapterTitle}}
                </a>
              }@else{
                <button class="primary btn-read-main" (mouseenter)="preloadFirstChapter()" (click)="readFirst()" [disabled]="reading()">
                  <app-icon name="book"/> {{reading() ? 'Đang mở…' : 'Đọc từ đầu'}}
                </button>
              }

              <button class="secondary btn-follow" [class.followed]="store.isFollowed(m.id)" (click)="follow()" [disabled]="following()">
                <app-icon [name]="store.isFollowed(m.id) ? 'check' : 'heart'"/>
                <span>{{store.isFollowed(m.id) ? 'Đang theo dõi' : 'Theo dõi'}}</span>
              </button>
            </div>

            <dl class="meta-grid">
              <dt>Tác giả</dt>
              <dd>{{m.author || 'Đang cập nhật'}}</dd>
              <dt>Tình trạng</dt>
              <dd>
                <span class="status-indicator" [class.ongoing]="m.status === 'ongoing'" [class.completed]="m.status === 'completed'">
                  {{statuses[m.status] || m.status}}
                </span>
                @if(m.year){ <small class="year">{{m.year}}</small> }
              </dd>
              <dt>Quốc gia</dt>
              <dd>{{countries[m.country] || m.country}}</dd>
              <dt>Độ tuổi</dt>
              <dd>{{m.contentRating === 'safe' ? 'Mọi lứa tuổi' : m.contentRating === 'suggestive' ? '16+' : '18+'}}</dd>
            </dl>

            <div class="tags">
              @for(g of m.genres; track g){
                <button class="tag-pill" [class.tag-romance]="g.toLowerCase() === 'romance'" (click)="findGenre(g)">{{g}}</button>
              }
            </div>
          </div>
        </div>
      </section>

      <!-- 2. SUMMARY / DESCRIPTION PANEL -->
      <section class="panel summary glass-panel">
        <div class="panel-heading">
          <h2><app-icon name="book"/> Tóm tắt nội dung</h2>
        </div>
        <p class="description" [class.expanded]="expanded">{{cleanDescription(m.description)}}</p>
        @if(cleanDescription(m.description).length > 250){
          <button class="text-button btn-expand-desc" (click)="expanded = !expanded">
            {{expanded ? 'Thu gọn bớt' : 'Xem toàn bộ mô tả'}} ⌄
          </button>
        }

        <!-- 3. CHAPTERS LIST SECTION -->
        <div class="section-title chapters-header">
          <h2><app-icon name="menu"/> Danh sách chương ({{chapterTotal()}})</h2>
          <div class="chapter-options">
            <select aria-label="Ngôn ngữ chương" [(ngModel)]="language" (ngModelChange)="chapterPage.set(1); loadChapters(); preloadFirstChapter()">
              <option value="vi">🇻🇳 Tiếng Việt</option>
              <option value="en">🇬🇧 Tiếng Anh</option>
            </select>
            <button class="secondary btn-sort-chap" (click)="ascending = !ascending; chapterPage.set(1); loadChapters()" [title]="ascending ? 'Đang hiển thị cũ đến mới' : 'Đang hiển thị mới đến cũ'">
              {{ascending ? '↑ Cũ → Mới' : '↓ Mới → Cũ'}}
            </button>
          </div>
        </div>

        @if(chapterLoading()){
          <div class="chapter-loading-box">
            <div class="search-spinner"></div>
            <span>Đang tải danh sách chương…</span>
          </div>
        }@else{
          <div class="chapter-list">
            @for(c of filteredChapters(); track c.id){
              <a [routerLink]="['/chuong', c.id]" class="chapter-row" [class.is-read]="isChapterRead(c.id)">
                <div class="chapter-main-info">
                  <span class="chapter-title-text">{{c.title}}</span>
                </div>
                <div class="chapter-meta-right">
                  <time>{{ago(c.publishedAt)}}</time>
                  <app-icon name="chevron"/>
                </div>
              </a>
            }
          </div>

          @if(!chapters().length){
            <div class="empty-small">
              {{chapterError() || 'Không có chương ở ngôn ngữ này. Hãy thử đổi ngôn ngữ sang Tiếng Anh hoặc quay lại sau.'}}
              @if(chapterError()){
                <button class="primary" style="margin-top: 10px;" (click)="loadChapters()">Thử lại</button>
              }
            </div>
          }

          @if(chapterTotal() > 100){
            <app-pagination [page]="chapterPage()" [total]="chapterTotal()" [size]="100" [change]="changeChapterPage"/>
          }
        }
      </section>

      <!-- 4. RATING PANEL -->
      <section class="panel glass-panel">
        <div class="section-title">
          <h2><app-icon name="star"/> Đánh giá của bạn</h2>
          <small class="muted">Cộng đồng: {{community().rating.toFixed(1)}} ★ ({{community().votes}} lượt)</small>
        </div>
        <div class="rating-buttons">
          @for(n of scores; track n){
            <button [class.active]="community().myRating === n" [attr.aria-label]="'Đánh giá ' + n + ' điểm'" (click)="rate(n)">
              {{n}}
            </button>
          }
        </div>
      </section>

      <!-- 5. COMMENTS PANEL -->
      <section class="panel glass-panel">
        <div class="section-title">
          <h2><app-icon name="comment"/> Bình luận <small class="muted">({{commentsTotal()}})</small></h2>
        </div>

        @if(store.user()){
          <form class="comment-form" (ngSubmit)="postComment()">
            <textarea name="comment" [(ngModel)]="body" rows="3" maxlength="2000" placeholder="Cảm nghĩ của bạn về bộ truyện này…" required></textarea>
            <div class="comment-submit">
              <small class="muted">{{body.length}} / 2.000 ký tự</small>
              <button class="primary" [disabled]="posting() || !body.trim()">
                {{posting() ? 'Đang gửi…' : 'Gửi bình luận'}}
              </button>
            </div>
          </form>
        }@else{
          <div class="login-prompt glass-panel">
            <app-icon name="user"/>
            <p><a routerLink="/dang-nhap" [queryParams]="{returnUrl: router.url}">Đăng nhập</a> để tham gia thảo luận cùng cộng đồng.</p>
          </div>
        }

        <div class="comment-list">
          @for(c of comments(); track c.id){
            <div class="comment-item">
              <span class="avatar">{{c.name.slice(0, 1).toUpperCase()}}</span>
              <div class="comment-content">
                <div class="comment-header">
                  <strong>{{c.name}}</strong>
                  <small>{{ago(c.createdAt)}} trước</small>
                </div>
                <p>{{c.body}}</p>
                @if(store.user()?.id === c.userId || store.user()?.role === 'admin'){
                  <button class="text-button btn-delete-comment" (click)="deleteComment(c.id)">Xóa bình luận</button>
                }
              </div>
            </div>
          }
          @if(!comments().length){
            <p class="empty-small muted">Chưa có bình luận nào. Hãy là người đầu tiên chia sẻ cảm nghĩ!</p>
          }
        </div>

        @if(commentsTotal() > 20){
          <app-pagination [page]="commentPage()" [total]="commentsTotal()" [size]="20" [change]="changeCommentPage"/>
        }
      </section>
    </div>

    <!-- Sidebar for Desktop -->
    <app-sidebar/>
  </div>
}
`
})
export class Detail {
  api = inject(Api);
  store = inject(Store);
  route = inject(ActivatedRoute);
  router = inject(Router);
  titleService = inject(Title);
  metaService = inject(Meta);

  manga = signal<Manga | null>(null);
  loading = signal(true);
  error = signal('');
  chapters = signal<Chapter[]>([]);
  chapterPage = signal(1);
  chapterTotal = signal(0);
  chapterLoading = signal(false);
  chapterError = signal('');
  firstChapter = signal<Chapter | null>(null);

  comments = signal<Comment[]>([]);
  commentsTotal = signal(0);
  commentPage = signal(1);
  community = signal({ rating: 0, votes: 0, myRating: 0 });

  posting = signal(false);
  following = signal(false);
  reading = signal(false);
  preloaded = false;
  language = this.store.settings().language;
  ascending = false;
  expanded = false;
  body = '';
  id = '';
  epoch = 0;
  chapterEpoch = 0;
  scores = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
  statuses = statuses;
  cleanDescription = cleanDescription;
  compact = compact;
  ago = ago;
  countries: Record<string, string> = { ja: 'Nhật Bản', ko: 'Hàn Quốc', zh: 'Trung Quốc', en: 'Tiếng Anh', vi: 'Việt Nam' };

  constructor() {
    this.route.paramMap.subscribe(p => {
      this.id = p.get('id')!;
      this.chapterPage.set(1);
      this.commentPage.set(1);
      this.firstChapter.set(null);
      this.preloaded = false;

      // Check cache for instant rendering
      const cachedManga = this.api.getCached<Manga>('/catalog/' + this.id);
      if (cachedManga) {
        this.manga.set(cachedManga);
        this.titleService.setTitle(`${cachedManga.title} [Mới Nhất] - Đọc Truyện Tranh | AkaTruyen`);
        this.loading.set(false);
      } else {
        this.manga.set(null);
        this.loading.set(true);
      }

      const chapPath = `/catalog/${this.id}/chapters?` + this.api.query({
        page: 1,
        language: this.language,
        ascending: this.ascending
      });
      const cachedChaps = this.api.getCached<Page<Chapter>>(chapPath);
      if (cachedChaps) {
        this.chapters.set(cachedChaps.items);
        this.chapterTotal.set(cachedChaps.total);
        this.chapterLoading.set(false);
      } else {
        this.chapters.set([]);
        this.chapterLoading.set(true);
      }

      void this.loadAll();
    });
  }

  async loadAll() {
    const n = ++this.epoch;
    this.error.set('');
    
    // Launch all independent API calls concurrently in parallel
    const mangaPromise = this.loadManga(n);
    const chapPromise = this.loadChapters();
    const commPromise = this.loadCommunity();
    const commentsPromise = this.loadComments();

    await Promise.allSettled([mangaPromise, chapPromise, commPromise, commentsPromise]);
    setTimeout(() => void this.preloadFirstChapter(), 1000);
  }

  async loadManga(n: number) {
    try {
      const m = await this.api.request<Manga>('/catalog/' + this.id, 'GET', undefined, true);
      if (n === this.epoch) {
        this.manga.set(m);
        this.titleService.setTitle(`${m.title} [Mới Nhất] - Đọc Truyện Tranh | AkaTruyen`);
        const desc = this.cleanDescription(m.description) || `Đọc truyện tranh ${m.title} tiếng Việt mới nhất online tại AkaTruyen (akatruyen.com).`;
        this.metaService.updateTag({ name: 'description', content: desc.slice(0, 160) });
      }
    } catch (e) {
      if (n === this.epoch && !this.manga()) {
        this.error.set(message(e));
      }
    } finally {
      if (n === this.epoch) this.loading.set(false);
    }
  }

  async load() {
    await this.loadAll();
  }

  async preloadFirstChapter() {
    if (this.preloaded) return;
    try {
      const r = await this.api.request<Page<Chapter>>(`/catalog/${this.id}/chapters?language=${this.language}&ascending=true&page=1`, 'GET', undefined, true);
      const first = r.items[0];
      if (!first) return;
      this.firstChapter.set(first);
      this.preloaded = true;
      const chapterData = await this.api.request<ReaderData>(`/chapters/${first.id}`);
      if (!chapterData) return;
      const pages = (this.store.settings().dataSaver && chapterData.dataSaverPages?.length ? chapterData.dataSaverPages : chapterData.pages) || [];
      pages.slice(0, 4).forEach(url => {
        const img = new Image();
        img.src = proxyImage(url);
      });
    } catch { }
  }

  async loadChapters() {
    const n = ++this.chapterEpoch;
    const chapPath = `/catalog/${this.id}/chapters?` + this.api.query({
      page: this.chapterPage(),
      language: this.language,
      ascending: this.ascending
    });

    const cached = this.api.getCached<Page<Chapter>>(chapPath);
    if (cached && cached.items) {
      this.chapters.set(cached.items);
      this.chapterTotal.set(cached.total);
      this.chapterLoading.set(false);
      return;
    }

    this.chapterLoading.set(true);
    this.chapterError.set('');
    try {
      const r = await this.api.request<Page<Chapter>>(chapPath, 'GET', undefined, true);
      if (n === this.chapterEpoch) {
        this.chapters.set(r.items);
        this.chapterTotal.set(r.total);
      }
    } catch (e) {
      if (n === this.chapterEpoch && !this.chapters().length) {
        this.chapters.set([]);
        this.chapterError.set(message(e));
      }
    } finally {
      if (n === this.chapterEpoch) this.chapterLoading.set(false);
    }
  }

  filteredChapters(): Chapter[] {
    return this.chapters();
  }

  isChapterRead(chapterId: string): boolean {
    return this.store.history().some(h => h.mangaId === this.id && h.chapterId === chapterId);
  }

  changeChapterPage = (p: number) => {
    this.chapterPage.set(p);
    void this.loadChapters();
  };

  changeCommentPage = (p: number) => {
    this.commentPage.set(p);
    void this.loadComments();
  };

  async loadComments() {
    try {
      const r = await this.api.request<Page<Comment>>(`/comments?mangaId=${this.id}&page=${this.commentPage()}`);
      this.comments.set(r.items);
      this.commentsTotal.set(r.total);
    } catch (e) {
      this.store.notify(message(e));
    }
  }

  async loadCommunity() {
    try {
      this.community.set(await this.api.request(`/catalog/${this.id}/community`));
    } catch { }
  }

  resume() {
    return this.store.history().find(x => x.mangaId === this.id);
  }

  async follow() {
    if (!this.store.user()) {
      void this.router.navigate(['/dang-nhap'], { queryParams: { returnUrl: this.router.url } });
      return;
    }
    this.following.set(true);
    try {
      await this.store.follow(this.manga()!);
    } catch (e) {
      this.store.notify(message(e));
    } finally {
      this.following.set(false);
    }
  }

  async readFirst() {
    const first = this.firstChapter();
    if (first) {
      void this.router.navigate(['/chuong', first.id]);
      return;
    }
    this.reading.set(true);
    try {
      const r = await this.api.request<Page<Chapter>>(`/catalog/${this.id}/chapters?language=${this.language}&ascending=true&page=1`, 'GET', undefined, true);
      if (r.items[0]) {
        this.firstChapter.set(r.items[0]);
        void this.router.navigate(['/chuong', r.items[0].id]);
      } else {
        this.store.notify('Chưa có chương ở ngôn ngữ này. Hãy thử đổi sang ngôn ngữ Tiếng Anh.');
      }
    } catch (e) {
      this.store.notify(message(e));
    } finally {
      this.reading.set(false);
    }
  }

  async findGenre(name: string) {
    try {
      const tags = await this.api.request<{ id: string; name: string }[]>('/catalog/tags');
      const t = tags.find(x => x.name === name);
      if (t) void this.router.navigate(['/tim-truyen-nang-cao'], { queryParams: { genre: t.id } });
    } catch (e) {
      this.store.notify(message(e));
    }
  }

  async rate(score: number) {
    if (!this.store.user()) {
      this.store.notify('Vui lòng đăng nhập để đánh giá.');
      return;
    }
    try {
      await this.api.request(`/catalog/${this.id}/rating`, 'PUT', { score });
      await this.loadCommunity();
      this.store.notify('Đã lưu đánh giá ' + score + ' điểm của bạn!');
    } catch (e) {
      this.store.notify(message(e));
    }
  }

  async postComment() {
    this.posting.set(true);
    try {
      await this.api.request(`/catalog/${this.id}/comments`, 'POST', { body: this.body });
      this.body = '';
      this.commentPage.set(1);
      await this.loadComments();
      this.store.notify('Đã gửi bình luận thành công.');
    } catch (e) {
      this.store.notify(message(e));
    } finally {
      this.posting.set(false);
    }
  }

  async deleteComment(id: string) {
    try {
      await this.api.request('/comments/' + id, 'DELETE');
      await this.loadComments();
    } catch (e) {
      this.store.notify(message(e));
    }
  }

  goBack(e?: Event) {
    if (e) e.preventDefault();
    if (typeof window !== 'undefined') {
      if (window.history.length > 1) {
        window.history.back();
      } else {
        void this.router.navigate(['/']);
      }
    }
  }
}
