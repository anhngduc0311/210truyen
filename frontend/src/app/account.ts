import { Component, inject, signal, AfterViewInit, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Store, message, ago, setStoredToken } from './core';
import { Icon } from './ui';

@Component({
  selector: 'app-auth',
  imports: [FormsModule, RouterLink, Icon],
  template: `
<div class="auth-wrap">
  <section class="auth-panel glass-panel">
    <a class="brand auth-brand" routerLink="/" aria-label="AkaTruyen - Trang chủ">
      <img class="brand-symbol" src="/brand-mark.svg" width="44" height="44" alt="" aria-hidden="true"><span class="brand-wordmark">aka<span>truyen</span><span class="brand-period">.</span></span>
    </a>
    <h1>{{register() ? 'Tạo tài khoản' : 'Chào mừng trở lại'}}</h1>
    <p class="muted">{{register() ? 'Lưu những bộ truyện yêu thích và tiếp tục hành trình đọc truyện của bạn.' : 'Đăng nhập để đồng bộ truyện theo dõi và lịch sử đọc trên mọi thiết bị.'}}</p>

    <!-- GOOGLE SIGN-IN BUTTON SECTION -->
    <div class="google-auth-section">
      <div id="google-btn-container" class="gsi-slot"></div>
      <button type="button" class="btn-google-login" (click)="loginWithGoogle()" [disabled]="busy()">
        <svg class="google-icon" viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"/>
          <path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"/>
          <path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z"/>
          <path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z"/>
        </svg>
        <span>{{register() ? 'Đăng ký nhanh bằng Google' : 'Đăng nhập bằng Google'}}</span>
      </button>
    </div>

    <div class="auth-divider">
      <span>hoặc sử dụng email</span>
    </div>

    <form class="auth-form" (ngSubmit)="submit()">
      @if(register()){
        <label>
          <span>Tên hiển thị</span>
          <input name="name" [(ngModel)]="name" required minlength="2" maxlength="60" autocomplete="nickname" placeholder="Ví dụ: Hoàng Long">
        </label>
      }

      <label>
        <span>{{register() ? 'Địa chỉ Email' : 'Email hoặc Tên tài khoản'}}</span>
        <input [type]="register() ? 'email' : 'text'" name="email" [(ngModel)]="email" required maxlength="254" [autocomplete]="register() ? 'email' : 'username'" [placeholder]="register() ? 'name@example.com' : 'admin hoặc email'">
      </label>

      <label>
        <span>Mật khẩu</span>
        <input type="password" name="password" [(ngModel)]="password" required [minlength]="register() ? 6 : 1" maxlength="128" [autocomplete]="register() ? 'new-password' : 'current-password'" placeholder="Nhập mật khẩu">
      </label>

      @if(register()){
        <label>
          <span>Xác nhận mật khẩu</span>
          <input type="password" name="confirm" [(ngModel)]="confirm" required autocomplete="new-password" placeholder="Nhập lại mật khẩu">
        </label>
      }

      @if(error()){
        <p class="error" role="alert">{{error()}}</p>
      }

      <button class="primary btn-auth-submit" [disabled]="busy()">
        {{busy() ? 'Đang xử lý…' : register() ? 'Đăng ký tài khoản' : 'Đăng nhập'}} 
        <app-icon name="arrowRight"/>
      </button>
    </form>

    <div class="auth-switch-row">
      <span>{{register() ? 'Đã có tài khoản?' : 'Chưa có tài khoản?'}}</span>
      <a [routerLink]="register() ? '/dang-nhap' : '/dang-ky'" [queryParams]="{returnUrl: returnUrl}">
        {{register() ? 'Đăng nhập ngay' : 'Đăng ký ngay'}}
      </a>
    </div>
    <small class="auth-note muted">AkaTruyen · Nền tảng đọc truyện online miễn phí</small>
  </section>
</div>`
})
export class Auth implements AfterViewInit, OnDestroy {
  store = inject(Store);
  route = inject(ActivatedRoute);
  router = inject(Router);

  googleClientId = signal('');

  register = signal(false);
  busy = signal(false);
  error = signal('');
  name = '';
  email = '';
  password = '';
  confirm = '';
  returnUrl = '/';

  private resizeObserver?: ResizeObserver;

  constructor() {
    this.route.url.subscribe(() => {
      this.register.set(this.router.url.startsWith('/dang-ky'));
      this.error.set('');
      setTimeout(() => this.renderGoogleButton(), 50);
    });
    const ret = this.route.snapshot.queryParamMap.get('returnUrl');
    if (ret?.startsWith('/')) this.returnUrl = ret;

    const err = this.route.snapshot.queryParamMap.get('error');
    if (err) this.error.set(err);

    const token = this.route.snapshot.queryParamMap.get('token');
    if (token) {
      setStoredToken(token);
      this.busy.set(true);
      void this.store.restore().then(() => {
        this.store.notify('Đăng nhập Google thành công!');
        void this.router.navigateByUrl(this.returnUrl);
      }).catch(e => {
        this.error.set(message(e));
      }).finally(() => {
        this.busy.set(false);
      });
    } else {
      void this.store.ensureRestored().then(user => {
        if (user && !err) {
          void this.router.navigateByUrl(this.returnUrl);
        }
      });
    }

    void this.loadGoogleConfig();
  }

  async loadGoogleConfig() {
    try {
      const res = await this.store.getGoogleConfig();
      if (res?.clientId) {
        this.googleClientId.set(res.clientId);
        this.initGoogleSignIn();
      }
    } catch { }
  }

  ngAfterViewInit() {
    this.initGoogleSignIn();
    if (typeof ResizeObserver !== 'undefined') {
      const el = document.getElementById('google-btn-container');
      if (el?.parentElement) {
        let lastWidth = Math.floor(el.parentElement.clientWidth);
        this.resizeObserver = new ResizeObserver(entries => {
          for (const entry of entries) {
            const w = Math.floor(entry.contentRect.width);
            if (Math.abs(w - lastWidth) > 16) {
              lastWidth = w;
              this.renderGoogleButton();
            }
          }
        });
        this.resizeObserver.observe(el.parentElement);
      }
    }
  }

  ngOnDestroy() {
    this.resizeObserver?.disconnect();
  }

  private initGoogleSignIn(retries = 10) {
    const cid = this.googleClientId();
    if (!cid) {
      if (retries > 0) setTimeout(() => this.initGoogleSignIn(retries - 1), 300);
      return;
    }
    const w = window as any;
    if (w.google?.accounts?.id) {
      try {
        w.google.accounts.id.initialize({
          client_id: cid,
          callback: (res: any) => {
            if (res?.credential) {
              void this.handleGoogleCredential(res.credential);
            }
          },
          auto_select: false,
          cancel_on_tap_outside: true
        });

        this.renderGoogleButton();
      } catch { }
    } else if (retries > 0) {
      setTimeout(() => this.initGoogleSignIn(retries - 1), 300);
    }
  }

  private renderGoogleButton() {
    const el = document.getElementById('google-btn-container');
    if (!el) return;
    const w = window as any;
    if (!w.google?.accounts?.id) return;

    el.innerHTML = '';
    const parent = el.parentElement || el;
    const availableWidth = parent.clientWidth || 320;
    // Google GIS allows width between 200 and 400
    const targetWidth = Math.max(200, Math.min(380, Math.floor(availableWidth)));
    const isLight = document.documentElement.getAttribute('data-theme') === 'light' || this.store.settings().theme === 'light';

    try {
      w.google.accounts.id.renderButton(el, {
        type: 'standard',
        shape: 'rectangular',
        theme: isLight ? 'outline' : 'filled_black',
        text: this.register() ? 'signup_with' : 'signin_with',
        size: 'large',
        locale: 'vi',
        width: targetWidth
      });
    } catch { }
  }

  loginWithGoogle() {
    this.busy.set(true);
    this.error.set('');

    const cid = this.googleClientId();
    const w = window as any;
    if (cid && w.google?.accounts?.id) {
      try {
        w.google.accounts.id.initialize({
          client_id: cid,
          callback: (res: any) => {
            if (res?.credential) {
              void this.handleGoogleCredential(res.credential);
            }
          }
        });
        w.google.accounts.id.prompt((notification: any) => {
          if (notification.isNotDisplayed() || notification.isSkippedMoment()) {
            this.redirectToGoogleOAuth();
          }
        });
        return;
      } catch {
        this.redirectToGoogleOAuth();
        return;
      }
    }
    this.redirectToGoogleOAuth();
  }

  redirectToGoogleOAuth() {
    window.location.href = `/api/auth/google/login?returnUrl=${encodeURIComponent(this.returnUrl)}`;
  }

  async handleGoogleCredential(credential: string) {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.store.loginWithGoogle(credential);
      this.store.notify(this.register() ? 'Đăng ký Google thành công!' : 'Đăng nhập Google thành công!');
      void this.router.navigateByUrl(this.returnUrl);
    } catch (e) {
      this.error.set(message(e));
    } finally {
      this.busy.set(false);
    }
  }

  async submit() {
    if (this.register() && this.password !== this.confirm) {
      this.error.set('Mật khẩu xác nhận chưa trùng khớp.');
      return;
    }
    this.busy.set(true);
    this.error.set('');
    try {
      await this.store.login(this.email, this.password, this.register() ? this.name : undefined);
      this.store.notify(this.register() ? 'Đăng ký và đăng nhập thành công!' : 'Đăng nhập thành công!');
      void this.router.navigateByUrl(this.returnUrl);
    } catch (e) {
      this.error.set(message(e));
    } finally {
      this.busy.set(false);
    }
  }
}

@Component({
  selector: 'app-library',
  imports: [RouterLink, Icon],
  template: `
<div class="library-page-container">
  <div class="section-title">
    <h1>
      <app-icon [name]="history() ? 'clock' : 'heart'"/> 
      {{history() ? 'Lịch sử đọc truyện' : 'Truyện đang theo dõi'}}
    </h1>
    <span class="muted total-count-pill">{{items().length}} bộ</span>
  </div>

  <div class="library-sub-bar">
    <p class="muted">{{store.user() ? 'Dữ liệu được đồng bộ an toàn với tài khoản của bạn.' : 'Dữ liệu được lưu trên trình duyệt này. Hãy đăng nhập để đồng bộ giữa máy tính và điện thoại.'}}</p>
    
    <div class="library-nav-pills">
      <a routerLink="/theo-doi" [class.active]="!history()" class="lib-pill-btn">
        <app-icon name="heart"/> Theo dõi ({{store.follows().length}})
      </a>
      <a routerLink="/lich-su" [class.active]="history()" class="lib-pill-btn">
        <app-icon name="clock"/> Lịch sử ({{store.history().length}})
      </a>
    </div>
  </div>

  @if(!history() && !store.user() && !items().length){
    <div class="empty-state glass-panel">
      <app-icon name="heart"/>
      <h2>Giữ những bộ truyện bạn yêu thích</h2>
      <p>Đăng nhập để theo dõi truyện và nhận thông báo khi có chương mới.</p>
      <a class="primary" routerLink="/dang-nhap" [queryParams]="{returnUrl: '/theo-doi'}">Đăng nhập ngay</a>
    </div>
  }@else{
    <div class="library-grid">
      @for(item of items(); track item.mangaId){
        <article class="library-card glass-panel">
          <a class="lib-cover-link" [routerLink]="['/truyen-tranh', item.mangaId]">
            <img [src]="item.cover" [alt]="item.title" referrerpolicy="no-referrer" loading="lazy">
          </a>
          <div class="lib-content">
            <h2><a [routerLink]="['/truyen-tranh', item.mangaId]">{{item.title}}</a></h2>
            
            @if(history()){
              <p class="lib-chap-info"><app-icon name="play" style="width:11px;height:11px;"/> {{item.chapterTitle}}</p>
              <small class="muted">{{ago(item.readAt!)}} trước</small>
              <div class="lib-actions">
                <a class="primary btn-read-continue" [routerLink]="['/chuong', item.chapterId]">
                  Đọc tiếp <app-icon name="arrowRight" style="width:12px;height:12px;"/>
                </a>
                <button class="text-button btn-remove-item" (click)="remove(item.mangaId)">Xóa</button>
              </div>
            }@else{
              <div class="lib-actions">
                <a class="primary btn-read-continue" [routerLink]="['/truyen-tranh', item.mangaId]">
                  Xem truyện <app-icon name="chevron"/>
                </a>
              </div>
            }
          </div>
        </article>
      }
    </div>

    @if(!items().length){
      <div class="empty-state glass-panel">
        <app-icon name="book"/>
        <h2>{{history() ? 'Bạn chưa đọc bộ truyện nào' : 'Bạn chưa theo dõi bộ truyện nào'}}</h2>
        <p>Hàng ngàn bộ truyện hấp dẫn đang chờ đón bạn khám phá.</p>
        <a class="primary" routerLink="/">Khám phá truyện hay</a>
      </div>
    }
  }
</div>`
})
export class Library {
  store = inject(Store);
  router = inject(Router);
  route = inject(ActivatedRoute);

  history = signal(false);
  ago = ago;

  constructor() {
    this.route.url.subscribe(() => this.history.set(this.router.url.includes('lich-su')));
    if (this.store.user()) {
      void this.store.refreshLibrary().catch(e => this.store.notify(message(e)));
    }
  }

  items() {
    return this.history() ? this.store.history() : this.store.follows();
  }

  async remove(id: string) {
    try {
      await this.store.removeHistory(id);
    } catch (e) {
      this.store.notify(message(e));
    }
  }
}

@Component({
  selector: 'app-not-found',
  imports: [RouterLink, Icon],
  template: `
<div class="empty-state glass-panel">
  <app-icon name="book"/>
  <h1>Không tìm thấy trang</h1>
  <p>Đường dẫn này không tồn tại hoặc đã được chuyển sang địa chỉ mới.</p>
  <a class="primary" routerLink="/">Về trang chủ</a>
</div>`
})
export class NotFound {}
