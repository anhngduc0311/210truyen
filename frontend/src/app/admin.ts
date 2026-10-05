import { Component, signal, computed, inject, OnInit } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';
import { Api, Store, compact, ago } from './core';
import { Icon } from './ui';
import JSZip from 'jszip';

export interface AdminKpis {
  viewsToday: number;
  viewsWeek: number;
  viewsMonth: number;
  totalViews: number;
  totalMangas: number;
  mangasUpdatedToday: number;
  totalChapters: number;
  chaptersUpdatedToday: number;
  totalUsers: number;
  newUsersToday: number;
  newUsersWeek: number;
  totalCoins: number;
  revenueToday: number;
  revenueMonth: number;
}

export interface AdminOverview {
  kpis: AdminKpis;
  chartViews: { date: string; views: number }[];
  chartCoins: { date: string; coins: number }[];
  recentReports: any[];
  topMangas: any[];
  recentLogs: any[];
}

export interface AdminManga {
  id: string;
  title: string;
  alternativeTitle: string;
  author: string;
  artist: string;
  cover: string;
  status: string;
  sourceType: string;
  isHidden: boolean;
  isDraft: boolean;
  featured: boolean;
  views: number;
  country: string;
  genres: string[];
  chaptersCount: number;
  updatedAt: string;
}

export interface AdminChapter {
  id: string;
  mangaId: string;
  number: number;
  title: string;
  language: string;
  contentType: string;
  isLocked: boolean;
  coinPrice: number;
  unlockAt?: string;
  scheduledPublishAt?: string;
  publishedAt: string;
  pagesCount: number;
  hasContent: boolean;
  pages?: string[];
  content?: string;
}

export interface AdminUser {
  id: string;
  email: string;
  name: string;
  role: string;
  coins: number;
  isBanned: boolean;
  createdAt: string;
}

export interface AdminComment {
  id: string;
  mangaId: string;
  mangaTitle: string;
  userId: string;
  userName: string;
  userEmail: string;
  body: string;
  isFlagged: boolean;
  createdAt: string;
}

export interface AdminReport {
  id: string;
  type: string;
  reason: string;
  status: string;
  notes?: string;
  createdAt: string;
  resolvedAt?: string;
  userId?: string;
  userName: string;
  mangaId?: string;
  mangaTitle?: string;
  chapterId?: string;
  chapterTitle?: string;
}

export interface AdminTaxonomy {
  id: string;
  type: string;
  name: string;
  slug: string;
  description: string;
  createdAt: string;
}

export interface AdminLog {
  id: string;
  level: string;
  source: string;
  message: string;
  createdAt: string;
}

@Component({
  selector: 'app-admin',
  imports: [CommonModule, FormsModule, RouterLink, Icon],
  templateUrl: './admin.html',
  styleUrl: './admin.scss'
})
export class AdminComponent implements OnInit {
  api = inject(Api);
  store = inject(Store);
  router = inject(Router);

  // Active navigation tab
  activeTab = signal<'overview' | 'mangas' | 'chapters' | 'taxonomy' | 'users' | 'moderation' | 'logs'>('overview');

  // Loading states
  loading = signal(false);
  actionLoading = signal(false);

  // Overview data
  overview = signal<AdminOverview | null>(null);

  // Manga state
  mangas = signal<AdminManga[]>([]);
  mangaTotal = signal(0);
  mangaPage = signal(1);
  mangaQuery = signal('');
  mangaStatusFilter = signal('all');
  mangaSourceFilter = signal('all');
  mangaVisibilityFilter = signal('all');

  // Manga Edit/Create Modal
  mangaModalOpen = signal(false);
  mangaEditMode = signal(false);
  currentManga: any = {
    id: '',
    title: '',
    alternativeTitle: '',
    author: '',
    artist: '',
    cover: '',
    description: '',
    genres: [] as string[],
    genresInput: '',
    status: 'ongoing',
    sourceType: 'original',
    country: 'jp',
    demographic: 'shounen',
    year: 2026,
    featured: false,
    isDraft: false,
    isHidden: false,
    scanlationGroup: '',
    tagsInput: ''
  };

  // Chapter Management state
  selectedMangaId = signal<string>('');
  selectedManga = signal<AdminManga | null>(null);
  chapters = signal<AdminChapter[]>([]);
  chapterModalOpen = signal(false);
  chapterEditMode = signal(false);
  chapterUploadMode = signal<'urls' | 'files'>('urls');
  currentChapter: any = {
    id: '',
    mangaId: '',
    number: 1,
    title: '',
    language: 'vi',
    contentType: 'comic', // 'comic' | 'novel'
    isLocked: false,
    coinPrice: 0,
    unlockAt: '',
    scheduledPublishAt: '',
    pagesText: '', // for bulk image URLs
    pagesList: [] as string[],
    content: '' // rich text for novels
  };
  uploadingImages = signal(false);

  // Chapter Pagination & Search State
  chapterPage = signal(1);
  chapterPageSize = signal(20);
  chapterSearchQuery = signal('');

  getFilteredChaptersList(): AdminChapter[] {
    const q = this.chapterSearchQuery().trim().toLowerCase();
    const list = this.chapters();
    if (!q) return list;
    return list.filter(c => c.title.toLowerCase().includes(q) || String(c.number).includes(q));
  }

  getPagedChapters(): AdminChapter[] {
    const filtered = this.getFilteredChaptersList();
    const p = this.chapterPage();
    const size = this.chapterPageSize();
    if (size >= 9999) return filtered;
    return filtered.slice((p - 1) * size, p * size);
  }

  // Chapter Download State
  downloadModalOpen = signal(false);
  downloadRangeMode = signal<'range' | 'custom'>('range');
  downloadFromChap = signal<number>(1);
  downloadToChap = signal<number>(1);
  downloadSelectedChapIds = signal<string[]>([]);
  downloadFormat = signal<'single_zip' | 'multi_zip' | 'text'>('single_zip');
  downloadQuality = signal<'original' | 'saver'>('original');
  isDownloading = signal(false);
  downloadProgress = signal<number>(0);
  downloadStatus = signal<string>('');
  downloadLogs = signal<string[]>([]);
  downloadChapSearch = signal<string>('');
  downloadAbortController: AbortController | null = null;

  // Taxonomy state
  taxonomyType = signal<'genre' | 'tag' | 'author' | 'group'>('genre');
  taxonomyItems = signal<AdminTaxonomy[]>([]);
  taxonomyModalOpen = signal(false);
  taxonomyEditMode = signal(false);
  currentTaxonomy: any = { id: '', type: 'genre', name: '', slug: '', description: '' };

  // Users & RBAC state
  users = signal<AdminUser[]>([]);
  userTotal = signal(0);
  userPage = signal(1);
  userQuery = signal('');
  userRoleFilter = signal('all');
  userStatusFilter = signal('all');
  userCoinModalOpen = signal(false);
  selectedUserForCoins = signal<AdminUser | null>(null);
  coinAdjustAmount = signal<number>(1000);
  coinAdjustNote = signal<string>('Nạp xu khuyến mãi');

  // Moderation state
  moderationTab = signal<'comments' | 'reports' | 'keywords'>('reports');
  comments = signal<AdminComment[]>([]);
  commentTotal = signal(0);
  commentPage = signal(1);
  commentQuery = signal('');
  commentFlaggedOnly = signal(false);

  reports = signal<AdminReport[]>([]);
  reportTotal = signal(0);
  reportPage = signal(1);
  reportStatusFilter = signal('pending');
  reportTypeFilter = signal('all');
  reportResolveModalOpen = signal(false);
  selectedReport = signal<AdminReport | null>(null);
  reportResolveNotes = signal('');

  keywords = signal<{ id: string; keyword: string; action: string }[]>([]);
  newKeyword = signal('');

  // Logs state
  logs = signal<AdminLog[]>([]);
  logTotal = signal(0);
  logPage = signal(1);
  logLevelFilter = signal('all');
  logSourceFilter = signal('all');

  // Utilities
  compact = compact;
  ago = ago;
  Math = Math;

  async ngOnInit() {
    const ok = await this.checkPermission();
    if (ok) {
      void this.loadOverview();
    }
  }

  async checkPermission(): Promise<boolean> {
    await this.store.ensureRestored();
    const u = this.store.user();
    if (!u) {
      void this.router.navigate(['/dang-nhap'], { queryParams: { returnUrl: '/admin' } });
      return false;
    }
    const staffRoles = ['admin', 'superadmin', 'editor', 'translator'];
    if (!staffRoles.includes(u.role)) {
      this.store.notify('Bạn không có quyền truy cập trang quản trị.');
      void this.router.navigate(['/']);
      return false;
    }
    return true;
  }

  isFullAdmin(): boolean {
    const role = this.store.user()?.role;
    return role === 'admin' || role === 'superadmin';
  }

  switchTab(tab: 'overview' | 'mangas' | 'chapters' | 'taxonomy' | 'users' | 'moderation' | 'logs') {
    this.activeTab.set(tab);
    if (tab === 'overview') void this.loadOverview();
    else if (tab === 'mangas') void this.loadMangas();
    else if (tab === 'chapters') {
      if (this.selectedMangaId()) void this.loadChapters(this.selectedMangaId());
      else void this.loadMangas();
    }
    else if (tab === 'taxonomy') void this.loadTaxonomy();
    else if (tab === 'users') void this.loadUsers();
    else if (tab === 'moderation') void this.loadModeration();
    else if (tab === 'logs') void this.loadLogs();
  }

  // =========================================================================
  // 1. OVERVIEW
  // =========================================================================
  async loadOverview() {
    this.loading.set(true);
    try {
      const data = await this.api.request<AdminOverview>('/admin/overview');
      this.overview.set(data);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được số liệu tổng quan.');
    } finally {
      this.loading.set(false);
    }
  }

  // SVG Chart helpers
  getViewsChartPath(): string {
    const points = this.overview()?.chartViews;
    if (!points || points.length < 2) return '';
    const max = Math.max(...points.map(p => p.views), 10);
    const width = 600;
    const height = 180;
    const step = width / (points.length - 1);

    return points.map((p, i) => {
      const x = i * step;
      const y = height - (p.views / max) * (height - 30) - 15;
      return `${i === 0 ? 'M' : 'L'} ${x.toFixed(1)} ${y.toFixed(1)}`;
    }).join(' ');
  }

  getViewsChartArea(): string {
    const path = this.getViewsChartPath();
    if (!path) return '';
    return `${path} L 600 180 L 0 180 Z`;
  }

  // =========================================================================
  // 2. MANGA MANAGEMENT
  // =========================================================================
  async loadMangas() {
    this.loading.set(true);
    try {
      const res = await this.api.request<{ items: AdminManga[]; total: number }>(
        '/admin/mangas?' + this.api.query({
          page: this.mangaPage(),
          pageSize: 15,
          q: this.mangaQuery().trim(),
          status: this.mangaStatusFilter(),
          sourceType: this.mangaSourceFilter(),
          visibility: this.mangaVisibilityFilter()
        })
      );
      this.mangas.set(res.items);
      this.mangaTotal.set(res.total);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được danh sách truyện.');
    } finally {
      this.loading.set(false);
    }
  }

  // Unified Pagination Helpers
  getTotalPages(totalItems: number, pageSize: number = 15): number {
    return Math.max(1, Math.ceil((totalItems || 0) / (pageSize || 15)));
  }

  getPageNumbers(currentPage: number, totalItems: number, pageSize: number = 15): (number | string)[] {
    const total = this.getTotalPages(totalItems, pageSize);
    if (total <= 7) {
      return Array.from({ length: total }, (_, i) => i + 1);
    }
    if (currentPage <= 4) {
      return [1, 2, 3, 4, 5, '...', total];
    }
    if (currentPage >= total - 3) {
      return [1, '...', total - 4, total - 3, total - 2, total - 1, total];
    }
    return [1, '...', currentPage - 1, currentPage, currentPage + 1, '...', total];
  }

  openCreateMangaModal() {
    this.mangaEditMode.set(false);
    this.currentManga = {
      id: '',
      title: '',
      alternativeTitle: '',
      author: '',
      artist: '',
      cover: '',
      description: '',
      genres: [],
      genresInput: 'Action, Manhwa, Fantasy',
      status: 'ongoing',
      sourceType: 'original',
      country: 'vn',
      demographic: 'shounen',
      year: new Date().getFullYear(),
      featured: false,
      isDraft: false,
      isHidden: false,
      scanlationGroup: '',
      tagsInput: ''
    };
    this.mangaModalOpen.set(true);
  }

  async openEditMangaModal(m: AdminManga) {
    this.mangaEditMode.set(true);
    try {
      const full = await this.api.request<any>('/admin/mangas/' + m.id);
      this.currentManga = {
        ...full,
        genresInput: (full.genres || []).join(', '),
        tagsInput: (full.tags || []).join(', ')
      };
      this.mangaModalOpen.set(true);
    } catch (e: any) {
      this.store.notify(e.message || 'Không lấy được chi tiết truyện.');
    }
  }

  async saveManga() {
    if (!this.currentManga.title.trim()) {
      this.store.notify('Vui lòng nhập tên truyện.');
      return;
    }
    this.actionLoading.set(true);
    try {
      const genres = this.currentManga.genresInput
        .split(',')
        .map((g: string) => g.trim())
        .filter(Boolean);

      const tags = this.currentManga.tagsInput
        .split(',')
        .map((t: string) => t.trim())
        .filter(Boolean);

      const payload = {
        title: this.currentManga.title.trim(),
        alternativeTitle: this.currentManga.alternativeTitle?.trim() || '',
        author: this.currentManga.author?.trim() || '',
        artist: this.currentManga.artist?.trim() || '',
        cover: this.currentManga.cover?.trim() || '/cover-placeholder.svg',
        description: this.currentManga.description?.trim() || '',
        genres,
        status: this.currentManga.status,
        country: this.currentManga.country,
        demographic: this.currentManga.demographic,
        year: Number(this.currentManga.year) || 2026,
        featured: !!this.currentManga.featured,
        sourceType: this.currentManga.sourceType,
        isDraft: !!this.currentManga.isDraft,
        isHidden: !!this.currentManga.isHidden,
        scanlationGroup: this.currentManga.scanlationGroup?.trim() || '',
        tags
      };

      if (this.mangaEditMode()) {
        await this.api.request('/admin/mangas/' + this.currentManga.id, 'PUT', payload);
        this.store.notify('Đã cập nhật truyện thành công.');
      } else {
        await this.api.request('/admin/mangas', 'POST', payload);
        this.store.notify('Đã thêm truyện mới thành công.');
      }
      this.mangaModalOpen.set(false);
      void this.loadMangas();
    } catch (e: any) {
      this.store.notify(e.message || 'Lỗi khi lưu truyện.');
    } finally {
      this.actionLoading.set(false);
    }
  }

  async toggleMangaVisibility(m: AdminManga) {
    try {
      const res = await this.api.request<{ isHidden: boolean }>(`/admin/mangas/${m.id}/toggle-visibility`, 'PATCH');
      m.isHidden = res.isHidden;
      this.store.notify(m.isHidden ? 'Đã ẩn truyện.' : 'Đã hiển thị truyện.');
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể đổi trạng thái hiển thị.');
    }
  }

  async deleteManga(m: AdminManga) {
    if (!confirm(`Bạn có chắc chắn muốn xóa vĩnh viễn truyện "${m.title}" và toàn bộ chương liên quan?`)) return;
    try {
      await this.api.request('/admin/mangas/' + m.id, 'DELETE');
      this.store.notify('Đã xóa truyện thành công.');
      void this.loadMangas();
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể xóa truyện.');
    }
  }

  manageMangaChapters(m: AdminManga) {
    this.selectedMangaId.set(m.id);
    this.selectedManga.set(m);
    this.switchTab('chapters');
  }

  // =========================================================================
  // 3. CHAPTER MANAGEMENT
  // =========================================================================
  async loadChapters(mangaId: string) {
    if (!mangaId) return;
    this.loading.set(true);
    this.chapterPage.set(1);
    this.chapterSearchQuery.set('');
    try {
      const res = await this.api.request<AdminChapter[]>(`/admin/mangas/${mangaId}/chapters`);
      this.chapters.set(res);
      if (!this.selectedManga()) {
        const m = await this.api.request<any>('/admin/mangas/' + mangaId);
        this.selectedManga.set(m);
      }
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được danh sách chương.');
    } finally {
      this.loading.set(false);
    }
  }

  openCreateChapterModal() {
    if (!this.selectedMangaId()) {
      this.store.notify('Vui lòng chọn truyện trước khi thêm chương.');
      return;
    }
    const nextNumber = this.chapters().length > 0 ? Math.floor(this.chapters()[0].number) + 1 : 1;
    this.chapterEditMode.set(false);
    this.currentChapter = {
      id: '',
      mangaId: this.selectedMangaId(),
      number: nextNumber,
      title: `Chương ${nextNumber}`,
      language: 'vi',
      contentType: 'comic',
      isLocked: false,
      coinPrice: 0,
      unlockAt: '',
      scheduledPublishAt: '',
      pagesText: '',
      pagesList: [],
      content: ''
    };
    this.chapterModalOpen.set(true);
  }

  async openEditChapterModal(c: AdminChapter) {
    this.chapterEditMode.set(true);
    try {
      const full = await this.api.request<any>('/admin/chapters/' + c.id);
      this.currentChapter = {
        ...full,
        pagesText: (full.pages || []).join('\n'),
        pagesList: full.pages || []
      };
      this.chapterModalOpen.set(true);
    } catch (e: any) {
      this.store.notify(e.message || 'Không lấy được chi tiết chương.');
    }
  }

  onPagesTextInput() {
    const urls = this.currentChapter.pagesText
      .split('\n')
      .map((l: string) => l.trim())
      .filter((l: string) => l.startsWith('http') || l.startsWith('/uploads'));
    this.currentChapter.pagesList = urls;
  }

  async onFileUpload(event: Event) {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0) return;

    this.uploadingImages.set(true);
    try {
      const token = this.store.getToken();
      const formData = new FormData();
      for (let i = 0; i < input.files.length; i++) {
        formData.append('files', input.files[i]);
      }

      const res = await fetch('/api/admin/chapters/upload-images', {
        method: 'POST',
        headers: {
          ...(token ? { Authorization: `Bearer ${token}` } : {})
        },
        body: formData
      });

      if (!res.ok) throw new Error('Upload ảnh thất bại.');
      const data = await res.json();
      const newUrls = data.urls || [];
      this.currentChapter.pagesList = [...this.currentChapter.pagesList, ...newUrls];
      this.currentChapter.pagesText = this.currentChapter.pagesList.join('\n');
      this.store.notify(`Đã tải lên ${newUrls.length} ảnh thành công!`);
    } catch (e: any) {
      this.store.notify(e.message || 'Lỗi khi upload ảnh.');
    } finally {
      this.uploadingImages.set(false);
      input.value = '';
    }
  }

  removeChapterPage(index: number) {
    this.currentChapter.pagesList.splice(index, 1);
    this.currentChapter.pagesText = this.currentChapter.pagesList.join('\n');
  }

  async saveChapter() {
    this.actionLoading.set(true);
    try {
      const pages = this.currentChapter.contentType === 'comic'
        ? this.currentChapter.pagesList
        : [];

      const payload = {
        number: Number(this.currentChapter.number) || 1,
        title: this.currentChapter.title?.trim() || `Chương ${this.currentChapter.number}`,
        language: this.currentChapter.language || 'vi',
        pages,
        content: this.currentChapter.contentType === 'novel' ? this.currentChapter.content : null,
        contentType: this.currentChapter.contentType,
        isLocked: !!this.currentChapter.isLocked,
        coinPrice: Number(this.currentChapter.coinPrice) || 0,
        unlockAt: this.currentChapter.unlockAt ? new Date(this.currentChapter.unlockAt).toISOString() : null,
        scheduledPublishAt: this.currentChapter.scheduledPublishAt ? new Date(this.currentChapter.scheduledPublishAt).toISOString() : null
      };

      if (this.chapterEditMode()) {
        await this.api.request('/admin/chapters/' + this.currentChapter.id, 'PUT', payload);
        this.store.notify('Đã cập nhật chương thành công.');
      } else {
        await this.api.request(`/admin/mangas/${this.selectedMangaId()}/chapters`, 'POST', payload);
        this.store.notify('Đã thêm chương mới thành công.');
      }
      this.chapterModalOpen.set(false);
      void this.loadChapters(this.selectedMangaId());
    } catch (e: any) {
      this.store.notify(e.message || 'Lỗi khi lưu chương.');
    } finally {
      this.actionLoading.set(false);
    }
  }

  async deleteChapter(c: AdminChapter) {
    if (!confirm(`Xóa chương "${c.title}"?`)) return;
    try {
      await this.api.request('/admin/chapters/' + c.id, 'DELETE');
      this.store.notify('Đã xóa chương thành công.');
      void this.loadChapters(this.selectedMangaId());
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể xóa chương.');
    }
  }

  // =========================================================================
  // CHAPTER DOWNLOADER (TẢI TRUYỆN THEO CHƯƠNG)
  // =========================================================================
  openDownloadChaptersModal(singleChap?: AdminChapter) {
    if (!this.selectedMangaId() || this.chapters().length === 0) {
      this.store.notify('Không có danh sách chương để tải.');
      return;
    }

    const chapNums = this.chapters()
      .map(c => Number(c.number))
      .filter(n => !isNaN(n))
      .sort((a, b) => a - b);

    const minNum = chapNums.length ? chapNums[0] : 1;
    const maxNum = chapNums.length ? chapNums[chapNums.length - 1] : 1;

    if (singleChap) {
      this.downloadRangeMode.set('range');
      this.downloadFromChap.set(singleChap.number);
      this.downloadToChap.set(singleChap.number);
      this.downloadSelectedChapIds.set([singleChap.id]);
    } else {
      this.downloadRangeMode.set('range');
      this.downloadFromChap.set(minNum);
      this.downloadToChap.set(maxNum);
      this.downloadSelectedChapIds.set(this.chapters().map(c => c.id));
    }

    this.downloadFormat.set('single_zip');
    this.downloadQuality.set('original');
    this.isDownloading.set(false);
    this.downloadProgress.set(0);
    this.downloadStatus.set('');
    this.downloadLogs.set([]);
    this.downloadChapSearch.set('');
    this.downloadModalOpen.set(true);
  }

  setDownloadPreset(preset: 'all' | 'first10' | 'last10') {
    const sorted = [...this.chapters()].sort((a, b) => Number(a.number) - Number(b.number));
    if (!sorted.length) return;

    if (preset === 'all') {
      this.downloadFromChap.set(Number(sorted[0].number));
      this.downloadToChap.set(Number(sorted[sorted.length - 1].number));
      this.downloadSelectedChapIds.set(sorted.map(c => c.id));
    } else if (preset === 'first10') {
      const slice = sorted.slice(0, 10);
      this.downloadFromChap.set(Number(slice[0].number));
      this.downloadToChap.set(Number(slice[slice.length - 1].number));
      this.downloadSelectedChapIds.set(slice.map(c => c.id));
    } else if (preset === 'last10') {
      const slice = sorted.slice(-10);
      this.downloadFromChap.set(Number(slice[0].number));
      this.downloadToChap.set(Number(slice[slice.length - 1].number));
      this.downloadSelectedChapIds.set(slice.map(c => c.id));
    }
  }

  toggleSelectAllChapters(select: boolean) {
    if (select) {
      this.downloadSelectedChapIds.set(this.chapters().map(c => c.id));
    } else {
      this.downloadSelectedChapIds.set([]);
    }
  }

  toggleSelectChapter(id: string) {
    const cur = this.downloadSelectedChapIds();
    if (cur.includes(id)) {
      this.downloadSelectedChapIds.set(cur.filter(x => x !== id));
    } else {
      this.downloadSelectedChapIds.set([...cur, id]);
    }
  }

  isChapterSelected(id: string): boolean {
    return this.downloadSelectedChapIds().includes(id);
  }

  getFilteredDownloadChapters(): AdminChapter[] {
    const q = this.downloadChapSearch().trim().toLowerCase();
    const sorted = [...this.chapters()].sort((a, b) => Number(a.number) - Number(b.number));
    if (!q) return sorted;
    return sorted.filter(c => c.title.toLowerCase().includes(q) || String(c.number).includes(q));
  }

  getSelectedDownloadCount(): number {
    if (this.downloadRangeMode() === 'range') {
      const from = Math.min(this.downloadFromChap(), this.downloadToChap());
      const to = Math.max(this.downloadFromChap(), this.downloadToChap());
      return this.chapters().filter(c => Number(c.number) >= from && Number(c.number) <= to).length;
    }
    return this.downloadSelectedChapIds().length;
  }

  cancelDownload() {
    if (this.downloadAbortController) {
      this.downloadAbortController.abort();
      this.downloadAbortController = null;
    }
    this.isDownloading.set(false);
    this.downloadStatus.set('Đã dừng tiến trình tải xuống.');
    this.store.notify('Đã hủy tải xuống.');
  }

  private addDownloadLog(msg: string) {
    const time = new Date().toLocaleTimeString('vi-VN');
    this.downloadLogs.update(logs => [`[${time}] ${msg}`, ...logs.slice(0, 49)]);
  }

  private async fetchImageBlobWithProxy(url: string, signal?: AbortSignal): Promise<Blob> {
    let targetUrl = url;
    if (url.startsWith('http://') || url.startsWith('https://')) {
      targetUrl = `/api/catalog/image-proxy?url=${encodeURIComponent(url)}`;
    }
    const res = await fetch(targetUrl, { signal });
    if (!res.ok) {
      if (targetUrl !== url) {
        try {
          const direct = await fetch(url, { signal });
          if (direct.ok) return await direct.blob();
        } catch { }
      }
      throw new Error(`HTTP ${res.status}`);
    }
    return await res.blob();
  }

  private async getChapterDetailForDownload(chapterId: string): Promise<{ pages: string[]; content: string | null; title: string; number: number }> {
    // 1. Check local admin endpoint
    try {
      const full = await this.api.request<any>('/admin/chapters/' + chapterId);
      if (full && ((full.pages && full.pages.length > 0) || full.content)) {
        return {
          pages: full.pages || [],
          content: full.content || null,
          title: full.title || `Chương ${full.number}`,
          number: Number(full.number) || 1
        };
      }
    } catch { }

    // 2. Check public catalog reader endpoint
    try {
      const reader = await this.api.request<any>('/chapters/' + chapterId);
      if (reader) {
        const isSaver = this.downloadQuality() === 'saver';
        const pages = (isSaver && reader.dataSaverPages && reader.dataSaverPages.length > 0)
          ? reader.dataSaverPages
          : (reader.pages || []);
        return {
          pages,
          content: null,
          title: reader.chapter?.title || `Chương ${reader.chapter?.number || 1}`,
          number: Number(reader.chapter?.number) || 1
        };
      }
    } catch { }

    return { pages: [], content: null, title: 'Chương', number: 1 };
  }

  async startDownloadChapters() {
    let targetChapters: AdminChapter[] = [];
    if (this.downloadRangeMode() === 'range') {
      const from = Math.min(this.downloadFromChap(), this.downloadToChap());
      const to = Math.max(this.downloadFromChap(), this.downloadToChap());
      targetChapters = this.chapters()
        .filter(c => Number(c.number) >= from && Number(c.number) <= to)
        .sort((a, b) => Number(a.number) - Number(b.number));
    } else {
      const selectedSet = new Set(this.downloadSelectedChapIds());
      targetChapters = this.chapters()
        .filter(c => selectedSet.has(c.id))
        .sort((a, b) => Number(a.number) - Number(b.number));
    }

    if (!targetChapters.length) {
      this.store.notify('Vui lòng chọn ít nhất một chương hợp lệ để tải.');
      return;
    }

    this.isDownloading.set(true);
    this.downloadProgress.set(0);
    this.downloadLogs.set([]);
    this.downloadStatus.set(`Bắt đầu tải ${targetChapters.length} chương...`);
    this.downloadAbortController = new AbortController();
    const signal = this.downloadAbortController.signal;

    const mangaTitle = this.selectedManga()?.title || 'Truyen';
    const safeMangaTitle = mangaTitle.replace(/[/\\?%*:|"<>]/g, '_').trim();
    const firstNum = targetChapters[0].number;
    const lastNum = targetChapters[targetChapters.length - 1].number;

    try {
      this.addDownloadLog(`Khởi tạo gói tải cho "${mangaTitle}" (${targetChapters.length} chương)`);
      const rootZip = new JSZip();

      let totalPagesDownloaded = 0;
      const totalChaps = targetChapters.length;

      for (let cIdx = 0; cIdx < totalChaps; cIdx++) {
        if (signal.aborted) break;
        const chap = targetChapters[cIdx];
        const chapNumPad = String(chap.number).padStart(3, '0');
        const chapFolderName = `Chuong_${chapNumPad}`;

        this.downloadStatus.set(`[${cIdx + 1}/${totalChaps}] Đang tải ${chap.title}...`);
        this.addDownloadLog(`[${cIdx + 1}/${totalChaps}] Lấy dữ liệu: ${chap.title}`);

        const detail = await this.getChapterDetailForDownload(chap.id);

        if (detail.content) {
          // Novel chapter text
          rootZip.folder(chapFolderName)?.file(`${chapFolderName}.txt`, detail.content);
          this.addDownloadLog(`✓ Đã lưu nội dung văn bản cho ${chap.title}`);
        } else if (detail.pages && detail.pages.length > 0) {
          const chapFolder = rootZip.folder(chapFolderName);
          const totalPages = detail.pages.length;

          for (let pIdx = 0; pIdx < totalPages; pIdx++) {
            if (signal.aborted) break;
            const imgUrl = detail.pages[pIdx];
            const pageNumPad = String(pIdx + 1).padStart(3, '0');
            const ext = imgUrl.includes('.png') ? 'png' : imgUrl.includes('.webp') ? 'webp' : 'jpg';
            const fileName = `${pageNumPad}.${ext}`;

            this.downloadStatus.set(`[${cIdx + 1}/${totalChaps}] ${chap.title} • Ảnh ${pIdx + 1}/${totalPages}`);

            try {
              const blob = await this.fetchImageBlobWithProxy(imgUrl, signal);
              chapFolder?.file(fileName, blob);
              totalPagesDownloaded++;
            } catch (err: any) {
              if (signal.aborted) break;
              this.addDownloadLog(`⚠ Lỗi tải ảnh ${pIdx + 1} của ${chap.title}: ${err.message || ''}`);
            }

            // Update percentage progress
            const currentChapProgress = (pIdx + 1) / totalPages;
            const overall = ((cIdx + currentChapProgress) / totalChaps) * 85;
            this.downloadProgress.set(Math.round(overall));
          }
          this.addDownloadLog(`✓ Đã tải xong ${detail.pages.length} trang của ${chap.title}`);
        } else {
          this.addDownloadLog(`⚠ Không tìm thấy nội dung hình ảnh cho ${chap.title}`);
        }
      }

      if (signal.aborted) {
        return;
      }

      // Final packaging
      this.downloadStatus.set('Đang nén file ZIP... Vui lòng đợi trong giây lát.');
      this.addDownloadLog('Bắt đầu nén toàn bộ tệp thành định dạng ZIP...');
      this.downloadProgress.set(88);

      const zipBlob = await rootZip.generateAsync(
        {
          type: 'blob',
          compression: 'DEFLATE',
          compressionOptions: { level: 5 }
        },
        metadata => {
          this.downloadProgress.set(88 + Math.round(metadata.percent * 0.11));
          this.downloadStatus.set(`Đang nén ZIP: ${Math.round(metadata.percent)}%`);
        }
      );

      // Trigger download
      this.downloadProgress.set(100);
      this.downloadStatus.set('Nén xong! Đang lưu tệp về máy...');
      this.addDownloadLog('Hoàn tất đóng gói! Đang kích hoạt tải về máy.');

      const url = URL.createObjectURL(zipBlob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `[AkaTruyen]_${safeMangaTitle}_Chap_${firstNum}_den_${lastNum}.zip`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);

      this.downloadStatus.set(`Tải về thành công ${totalChaps} chương (${totalPagesDownloaded} ảnh)!`);
      this.store.notify(`Đã xuất file ZIP thành công (${totalChaps} chương)!`);
    } catch (e: any) {
      if (!signal.aborted) {
        this.downloadStatus.set(`Lỗi: ${e.message || 'Không thể tải chương.'}`);
        this.addDownloadLog(`❌ Lỗi: ${e.message || 'Lỗi không xác định'}`);
        this.store.notify('Có lỗi xảy ra khi tải chương.');
      }
    } finally {
      this.isDownloading.set(false);
      this.downloadAbortController = null;
    }
  }

  // =========================================================================
  // 4. TAXONOMY MANAGEMENT
  // =========================================================================
  async loadTaxonomy() {
    this.loading.set(true);
    try {
      const items = await this.api.request<AdminTaxonomy[]>('/admin/taxonomy?type=' + this.taxonomyType());
      this.taxonomyItems.set(items);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được danh mục taxonomy.');
    } finally {
      this.loading.set(false);
    }
  }

  openCreateTaxonomyModal() {
    this.taxonomyEditMode.set(false);
    this.currentTaxonomy = { id: '', type: this.taxonomyType(), name: '', slug: '', description: '' };
    this.taxonomyModalOpen.set(true);
  }

  openEditTaxonomyModal(t: AdminTaxonomy) {
    this.taxonomyEditMode.set(true);
    this.currentTaxonomy = { ...t };
    this.taxonomyModalOpen.set(true);
  }

  async saveTaxonomy() {
    if (!this.currentTaxonomy.name.trim()) {
      this.store.notify('Vui lòng nhập tên mục.');
      return;
    }
    this.actionLoading.set(true);
    try {
      if (this.taxonomyEditMode()) {
        await this.api.request('/admin/taxonomy/' + this.currentTaxonomy.id, 'PUT', this.currentTaxonomy);
        this.store.notify('Đã cập nhật taxonomy.');
      } else {
        await this.api.request('/admin/taxonomy', 'POST', this.currentTaxonomy);
        this.store.notify('Đã thêm taxonomy mới.');
      }
      this.taxonomyModalOpen.set(false);
      void this.loadTaxonomy();
    } catch (e: any) {
      this.store.notify(e.message || 'Lỗi khi lưu taxonomy.');
    } finally {
      this.actionLoading.set(false);
    }
  }

  async deleteTaxonomy(t: AdminTaxonomy) {
    if (!confirm(`Xóa mục "${t.name}"?`)) return;
    try {
      await this.api.request('/admin/taxonomy/' + t.id, 'DELETE');
      this.store.notify('Đã xóa taxonomy.');
      void this.loadTaxonomy();
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể xóa taxonomy.');
    }
  }

  // =========================================================================
  // 5. USERS & RBAC MANAGEMENT
  // =========================================================================
  async loadUsers() {
    this.loading.set(true);
    try {
      const res = await this.api.request<{ items: AdminUser[]; total: number }>(
        '/admin/users?' + this.api.query({
          page: this.userPage(),
          pageSize: 20,
          q: this.userQuery().trim(),
          role: this.userRoleFilter(),
          status: this.userStatusFilter()
        })
      );
      this.users.set(res.items);
      this.userTotal.set(res.total);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được danh sách thành viên.');
    } finally {
      this.loading.set(false);
    }
  }

  async updateUserRole(u: AdminUser, newRole: string) {
    try {
      await this.api.request(`/admin/users/${u.id}/role`, 'PATCH', { role: newRole });
      u.role = newRole;
      this.store.notify(`Đã cập nhật vai trò của ${u.name} thành "${newRole}".`);
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể đổi vai trò.');
    }
  }

  async toggleUserStatus(u: AdminUser) {
    try {
      const res = await this.api.request<{ isBanned: boolean }>(`/admin/users/${u.id}/status`, 'PATCH');
      u.isBanned = res.isBanned;
      this.store.notify(u.isBanned ? `Đã khóa tài khoản ${u.name}.` : `Đã mở khóa tài khoản ${u.name}.`);
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể đổi trạng thái tài khoản.');
    }
  }

  openUserCoinModal(u: AdminUser) {
    this.selectedUserForCoins.set(u);
    this.coinAdjustAmount.set(5000);
    this.coinAdjustNote.set('Cộng xu thưởng nạp / quà tặng admin');
    this.userCoinModalOpen.set(true);
  }

  async submitCoinAdjust() {
    const u = this.selectedUserForCoins();
    if (!u) return;
    this.actionLoading.set(true);
    try {
      const res = await this.api.request<{ coins: number }>(`/admin/users/${u.id}/coins`, 'POST', {
        amount: this.coinAdjustAmount(),
        description: this.coinAdjustNote()
      });
      u.coins = res.coins;
      this.store.notify(`Đã điều chỉnh số dư xu của ${u.name}: ${res.coins} xu.`);
      this.userCoinModalOpen.set(false);
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể điều chỉnh xu.');
    } finally {
      this.actionLoading.set(false);
    }
  }

  // =========================================================================
  // 6. MODERATION & REPORTS
  // =========================================================================
  async loadModeration() {
    if (this.moderationTab() === 'reports') void this.loadReports();
    else if (this.moderationTab() === 'comments') void this.loadComments();
    else if (this.moderationTab() === 'keywords') void this.loadKeywords();
  }

  async loadReports() {
    this.loading.set(true);
    try {
      const res = await this.api.request<{ items: AdminReport[]; total: number }>(
        '/admin/reports?' + this.api.query({
          page: this.reportPage(),
          pageSize: 20,
          status: this.reportStatusFilter(),
          type: this.reportTypeFilter()
        })
      );
      this.reports.set(res.items);
      this.reportTotal.set(res.total);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được báo cáo.');
    } finally {
      this.loading.set(false);
    }
  }

  openResolveReportModal(r: AdminReport) {
    this.selectedReport.set(r);
    this.reportResolveNotes.set('');
    this.reportResolveModalOpen.set(true);
  }

  async resolveReport(status: 'resolved' | 'dismissed') {
    const r = this.selectedReport();
    if (!r) return;
    this.actionLoading.set(true);
    try {
      await this.api.request(`/admin/reports/${r.id}`, 'PATCH', {
        status,
        notes: this.reportResolveNotes()
      });
      r.status = status;
      this.store.notify(status === 'resolved' ? 'Đã đánh dấu đã xử lý báo cáo.' : 'Đã bỏ qua báo cáo.');
      this.reportResolveModalOpen.set(false);
      void this.loadReports();
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể cập nhật báo cáo.');
    } finally {
      this.actionLoading.set(false);
    }
  }

  async loadComments() {
    this.loading.set(true);
    try {
      const res = await this.api.request<{ items: AdminComment[]; total: number }>(
        '/admin/comments?' + this.api.query({
          page: this.commentPage(),
          pageSize: 20,
          q: this.commentQuery().trim(),
          flaggedOnly: this.commentFlaggedOnly()
        })
      );
      this.comments.set(res.items);
      this.commentTotal.set(res.total);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được bình luận.');
    } finally {
      this.loading.set(false);
    }
  }

  async deleteComment(c: AdminComment) {
    if (!confirm('Xóa bình luận này?')) return;
    try {
      await this.api.request('/admin/comments/' + c.id, 'DELETE');
      this.comments.update(list => list.filter(item => item.id !== c.id));
      this.store.notify('Đã xóa bình luận.');
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể xóa bình luận.');
    }
  }

  async toggleFlagComment(c: AdminComment) {
    try {
      const res = await this.api.request<{ isFlagged: boolean }>(`/admin/comments/${c.id}/flag`, 'PATCH');
      c.isFlagged = res.isFlagged;
      this.store.notify(c.isFlagged ? 'Đã gắn cờ cảnh báo bình luận.' : 'Đã bỏ gắn cờ bình luận.');
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể gắn cờ bình luận.');
    }
  }

  async loadKeywords() {
    this.loading.set(true);
    try {
      const items = await this.api.request<{ id: string; keyword: string; action: string }[]>('/admin/keywords');
      this.keywords.set(items);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được danh sách từ khóa cấm.');
    } finally {
      this.loading.set(false);
    }
  }

  async addKeyword() {
    const kw = this.newKeyword().trim();
    if (!kw) return;
    try {
      await this.api.request('/admin/keywords', 'POST', { keyword: kw, action: 'block' });
      this.newKeyword.set('');
      this.store.notify('Đã thêm từ khóa cấm.');
      void this.loadKeywords();
    } catch (e: any) {
      this.store.notify(e.message || 'Lỗi thêm từ khóa.');
    }
  }

  async deleteKeyword(id: string) {
    try {
      await this.api.request('/admin/keywords/' + id, 'DELETE');
      this.store.notify('Đã xóa từ khóa cấm.');
      void this.loadKeywords();
    } catch (e: any) {
      this.store.notify(e.message || 'Không thể xóa từ khóa.');
    }
  }

  // =========================================================================
  // 7. SYSTEM LOGS
  // =========================================================================
  async loadLogs() {
    this.loading.set(true);
    try {
      const res = await this.api.request<{ items: AdminLog[]; total: number }>(
        '/admin/logs?' + this.api.query({
          page: this.logPage(),
          pageSize: 25,
          level: this.logLevelFilter(),
          source: this.logSourceFilter()
        })
      );
      this.logs.set(res.items);
      this.logTotal.set(res.total);
    } catch (e: any) {
      this.store.notify(e.message || 'Không tải được nhật ký log.');
    } finally {
      this.loading.set(false);
    }
  }
}
