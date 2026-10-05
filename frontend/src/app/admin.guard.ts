import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Store } from './core';

export const adminGuard: CanActivateFn = async () => {
  const store = inject(Store);
  const router = inject(Router);

  await store.ensureRestored();

  const u = store.user();
  if (u && ['admin', 'superadmin', 'editor', 'translator'].includes(u.role)) {
    return true;
  }

  if (!u) {
    return router.createUrlTree(['/dang-nhap'], { queryParams: { returnUrl: '/admin' } });
  }

  store.notify('Bạn không có quyền truy cập trang quản trị.');
  return router.createUrlTree(['/']);
};
