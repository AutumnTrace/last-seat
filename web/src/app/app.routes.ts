import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./home/home').then((m) => m.Home), title: 'Last Seat' },
  {
    path: 'book/:name',
    loadComponent: () => import('./booking/booking-page').then((m) => m.BookingPage),
    title: 'Last Seat: booking',
  },
  {
    path: 'box-office',
    loadComponent: () => import('./box-office/box-office').then((m) => m.BoxOffice),
    title: 'Last Seat: box office',
  },
  { path: '**', redirectTo: '' },
];
