import { Injectable, signal, computed } from '@angular/core';

export interface Chapter { id:string; mangaId:string; title:string; number:number; language:string; publishedAt:string; group:string; }
export interface Manga { id:string; title:string; alternativeTitle:string; author:string; cover:string; description:string; genres:string[]; status:string; country:string; demographic:string; contentRating:string; year:number|null; rating:number; follows:number; updatedAt:string; chapters:Chapter[]; totalChapters?:number; }
export interface Page<T> { items:T[]; total:number; page:number; pageSize:number; }
export interface Reader { chapter:Chapter; manga:Manga; pages:string[]; dataSaverPages:string[]; externalUrl:string|null; navigation:Chapter[]; }
export interface User { id:string; name:string; email:string; role:string; coins?:number; isBanned?:boolean; }
export interface LibraryItem { mangaId:string; title:string; cover:string; chapterId?:string; chapterTitle?:string; readAt?:string; }
export interface Comment { id:string; mangaId:string; mangaTitle:string; userId:string; name:string; body:string; createdAt:string; }
export interface Settings { language:string; dataSaver:boolean; width:number; theme:string; }
export function readStored<T>(key:string, fallback:T):T {
  try {
    if (typeof window === 'undefined') return fallback;
    const val = localStorage.getItem(key) || sessionStorage.getItem(key);
    return val ? (JSON.parse(val) ?? fallback) : fallback;
  } catch {
    return fallback;
  }
}

export function getStoredToken(): string | null {
  if (typeof window === 'undefined') return null;
  return sessionStorage.getItem('td-token') || localStorage.getItem('td-token');
}

export function setStoredToken(token: string, user?: User | null): void {
  if (typeof window === 'undefined') return;
  try {
    sessionStorage.setItem('td-token', token);
    localStorage.setItem('td-token', token);
    if (user) {
      sessionStorage.setItem('td-user', JSON.stringify(user));
      localStorage.setItem('td-user', JSON.stringify(user));
    }
  } catch {}
}

export function clearStoredAuth(): void {
  if (typeof window === 'undefined') return;
  try {
    sessionStorage.removeItem('td-token');
    sessionStorage.removeItem('td-user');
    localStorage.removeItem('td-token');
    localStorage.removeItem('td-user');
  } catch {}
}

export const message = (e:unknown) => e instanceof Error ? e.message : 'Không thể kết nối. Vui lòng thử lại.';
export function compact(n:number):string { return n>=1000000?(n/1000000).toFixed(1)+'m':n>=1000?(n/1000).toFixed(1)+'k':String(n); }
export function ago(date:string):string { const m=Math.max(0,Math.floor((Date.now()-new Date(date).getTime())/60000)); return m<1?'Vừa xong':m<60?`${m} phút`:m<1440?`${Math.floor(m/60)} giờ`:`${Math.floor(m/1440)} ngày`; }
export const statuses:Record<string,string>={ongoing:'Đang tiến hành',completed:'Đã hoàn thành',hiatus:'Tạm ngưng',cancelled:'Đã hủy'};

export function cleanDescription(desc: string | null | undefined): string {
  if (!desc) return 'Chưa có mô tả cho bộ truyện này.';
  let text = desc;
  text = text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&').replace(/&quot;/g, '"');
  text = text.replace(/<[^>]*>/g, ' ');
  text = text.replace(/Truyện tranh\s+.*?được cập nhật nhanh và đầy đủ nhất tại\s+\S+/gi, '');
  text = text.replace(/Bạn đọc đừng quên để lại bình luận và chia sẻ,\s*ủng hộ.*$/gmi, '');
  text = text.replace(/Đọc truyện\s+.*?tại\s+\S+/gi, '');
  text = text.replace(/Xem truyện\s+.*?tại\s+\S+/gi, '');
  text = text.split('\n').map(l => l.trim()).filter(Boolean).join('\n\n').trim();
  return text || 'Chưa có mô tả cho bộ truyện này.';
}

export function proxyImage(url: string): string {
  if (!url || url.startsWith('https://services.f-ck.me/') || url.startsWith('/api/')) return url;
  if (url.includes('.mangadex.network/') || url.includes('mangadex.org/')) {
    return 'https://services.f-ck.me/v1/image/' + btoa(url).replace(/\+/g, '-').replace(/\//g, '_');
  }
  if (url.includes('hinhtruyen.com') || url.includes('hinhhinh.com') || url.includes('truyenggvn.com') || url.includes('truyenvua') || url.includes('tintruyen') || url.includes('blogspot.com')) {
    return '/api/catalog/image-proxy?url=' + encodeURIComponent(url);
  }
  return url;
}

@Injectable({providedIn:'root'})
export class Api {
  private cache = new Map<string, { data: unknown; expiry: number }>();
  private preloadedImages = new Set<string>();

  hasCached(path: string): boolean {
    const hit = this.cache.get(path);
    return !!hit && hit.expiry > Date.now();
  }

  getCached<T>(path: string): T | undefined {
    const hit = this.cache.get(path);
    if (hit && hit.expiry > Date.now()) {
      return hit.data as T;
    }
    return undefined;
  }

  preloadImages(urls: string[]): void {
    if (typeof window === 'undefined') return;
    for (const u of urls) {
      if (!u || u === '/cover-placeholder.svg' || this.preloadedImages.has(u)) continue;
      this.preloadedImages.add(u);
      const img = new Image();
      img.referrerPolicy = 'no-referrer';
      img.src = u;
    }
  }

  prefetch(path: string): void {
    if (this.hasCached(path)) return;
    void this.request<Page<Manga>>(path, 'GET', undefined, true).then(res => {
      if (res && res.items && Array.isArray(res.items)) {
        const topCovers = res.items.slice(0, 6).map(m => m.cover).filter(Boolean);
        this.preloadImages(topCovers);
      }
    }).catch(() => {});
  }

  prefetchDetail(id: string, language = 'vi'): void {
    if (!id) return;
    const detailPath = '/catalog/' + id;
    if (!this.hasCached(detailPath)) {
      void this.request<Manga>(detailPath, 'GET', undefined, true).then(m => {
        if (m && m.cover) this.preloadImages([m.cover]);
      }).catch(() => {});
    }
    const chapPath = `/catalog/${id}/chapters?page=1&language=${language}&ascending=false`;
    if (!this.hasCached(chapPath)) {
      void this.request(chapPath, 'GET', undefined, true).catch(() => {});
    }
    const commPath = `/catalog/${id}/community`;
    if (!this.hasCached(commPath)) {
      void this.request(commPath, 'GET', undefined, true).catch(() => {});
    }
  }

  clearCache(prefix?: string) {
    if (!prefix) this.cache.clear();
    else { for (const k of this.cache.keys()) if (k.includes(prefix)) this.cache.delete(k); }
  }

  async request<T>(path:string, method='GET', body?:unknown, useCache = false):Promise<T> {
    const isGet = method === 'GET';
    const shouldCache = isGet && (useCache || path.startsWith('/chapters/') || path.startsWith('/catalog/'));
    if (shouldCache) {
      const hit = this.cache.get(path);
      if (hit && hit.expiry > Date.now()) {
        return hit.data as T;
      }
    }
    const token = getStoredToken();
    const maxRetries = isGet ? 2 : 0;
    let lastError: unknown;

    for (let attempt = 0; attempt <= maxRetries; attempt++) {
      try {
        if (attempt > 0) {
          await new Promise(r => setTimeout(r, attempt * 800));
        }
        const response = await fetch('/api' + path, {
          method,
          headers: {
            ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
            ...(token ? { Authorization: `Bearer ${token}` } : {})
          },
          body: body !== undefined ? JSON.stringify(body) : undefined
        });

        if (!response.ok) {
          if (isGet && attempt < maxRetries && [500, 502, 503, 504].includes(response.status)) {
            continue;
          }
          let info: any;
          try { info = await response.json(); } catch { }
          throw new Error(info?.message || (response.status === 401 ? 'Vui lòng đăng nhập để tiếp tục.' : response.status === 429 ? 'Bạn thao tác quá nhanh. Vui lòng thử lại sau một phút.' : 'Không tải được dữ liệu. Vui lòng thử lại.'));
        }

        const result = response.status === 204 ? undefined as T : await response.json();
        if (shouldCache) {
          const isSearch = path.startsWith('/catalog/search');
          const isEmpty = result && typeof result === 'object' && 'items' in (result as any) && Array.isArray((result as any).items) && (result as any).items.length === 0;
          if (!isSearch || !isEmpty) {
            this.cache.set(path, { data: result, expiry: Date.now() + 15 * 60 * 1000 });
          }
        }
        return result;
      } catch (err) {
        lastError = err;
        if (isGet && attempt < maxRetries && !(err instanceof Error && err.message.includes('đăng nhập'))) {
          continue;
        }
        throw err;
      }
    }
    throw lastError;
  }
  query(params:Record<string,unknown>){const q=new URLSearchParams();Object.entries(params).forEach(([k,v])=>{if(v!==''&&v!==undefined&&v!==null)q.set(k,String(v));});return q.toString();}
}
@Injectable({providedIn:'root'})
export class Store {
  user=signal<User|null>(readStored('td-user', null));
  follows=signal<LibraryItem[]>([]);
  history=signal<LibraryItem[]>(readStored('td-history',[]));
  settings=signal<Settings>(readStored('td-settings',{language:'vi',dataSaver:true,width:900,theme:'dark'}));
  toast=signal('');
  private timer?:ReturnType<typeof setTimeout>;
  private restorePromise: Promise<User | null> | null = null;

  constructor(private api:Api){
    this.applyTheme();
    if(getStoredToken()) void this.restore();
  }

  notify(text:string){this.toast.set(text);clearTimeout(this.timer);this.timer=setTimeout(()=>this.toast.set(''),4500);}

  hasToken(): boolean {
    return !!getStoredToken();
  }

  getToken(): string | null {
    return getStoredToken();
  }

  ensureRestored(): Promise<User | null> {
    if (!getStoredToken()) {
      return Promise.resolve(null);
    }
    if (this.restorePromise) {
      return this.restorePromise;
    }
    if (this.user()) {
      return Promise.resolve(this.user());
    }
    return this.restore();
  }

  async restore(): Promise<User | null> {
    const token = getStoredToken();
    if (!token) {
      this.user.set(null);
      return null;
    }
    if (this.restorePromise) return this.restorePromise;

    this.restorePromise = (async () => {
      try {
        const u = await this.api.request<User>('/auth/me');
        this.user.set(u);
        setStoredToken(token, u);
        await this.refreshLibrary();
        return u;
      } catch {
        clearStoredAuth();
        this.user.set(null);
        return null;
      } finally {
        this.restorePromise = null;
      }
    })();

    return this.restorePromise;
  }

  async login(email:string,password:string,name?:string){
    const s=await this.api.request<{token:string;user:User}>('/auth/'+(name!==undefined?'register':'login'),'POST',{email,password,name});
    setStoredToken(s.token, s.user);
    this.user.set(s.user);
    await this.refreshLibrary();
  }

  async loginWithGoogle(credential?: string, code?: string, redirectUri?: string){
    const s=await this.api.request<{token:string;user:User}>('/auth/google','POST',{credential,code,redirectUri});
    setStoredToken(s.token, s.user);
    this.user.set(s.user);
    await this.refreshLibrary();
  }

  async getGoogleConfig(){
    return await this.api.request<{clientId:string}>('/auth/google/config');
  }

  logout(){
    clearStoredAuth();
    this.user.set(null);
    this.follows.set([]);
    this.history.set(readStored('td-history',[]));
    this.notify('Đã đăng xuất.');
  }

  async refreshLibrary(){if(!this.user())return;const r=await this.api.request<{follows:LibraryItem[];history:LibraryItem[]}>('/library');this.follows.set(r.follows);this.history.set(r.history);}
  isFollowed(id:string){return this.follows().some(x=>x.mangaId===id);}
  async follow(m:Manga){if(!this.user())throw new Error('Vui lòng đăng nhập để theo dõi truyện.');const followed=!this.isFollowed(m.id);await this.api.request(`/library/follows/${m.id}`,'PUT',{followed});await this.refreshLibrary();this.notify(followed?'Đã thêm vào truyện theo dõi.':'Đã bỏ theo dõi truyện.');}
  async record(r:Reader){const item:LibraryItem={mangaId:r.manga.id,title:r.manga.title,cover:r.manga.cover,chapterId:r.chapter.id,chapterTitle:r.chapter.title,readAt:new Date().toISOString()};if(this.user()){await this.api.request(`/library/history/${r.chapter.id}`,'PUT',{});await this.refreshLibrary();}else{this.history.update(v=>[item,...v.filter(x=>x.mangaId!==item.mangaId)].slice(0,200));localStorage.setItem('td-history',JSON.stringify(this.history()));}}
  async removeHistory(id:string){if(this.user())await this.api.request('/library/history/'+id,'DELETE');this.history.update(v=>v.filter(x=>x.mangaId!==id));if(!this.user())localStorage.setItem('td-history',JSON.stringify(this.history()));}
  saveSettings(s:Settings){this.settings.set({...s});localStorage.setItem('td-settings',JSON.stringify(s));this.applyTheme();this.notify('Đã lưu tùy chỉnh.');}
  applyTheme(){document.documentElement.dataset['theme']=this.settings().theme;}
}
