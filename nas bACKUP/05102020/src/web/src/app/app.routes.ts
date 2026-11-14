import { Routes } from '@angular/router';
import { authGuard, screenGuard } from './core/auth';
import { ShellComponent } from './shell/shell.component';

const masters = () => import('./features/masters/masters.components');
const settings = () => import('./features/settings/settings.components');
const accounts = () => import('./features/accounts/accounts.components');
const tools = () => import('./features/tools/tools.components');
const faq = () => import('./features/faq/faq.components');

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/auth/auth.components').then(m => m.LoginComponent) },
  { path: 'setup', loadComponent: () => import('./features/auth/auth.components').then(m => m.SetupComponent) },
  {
    path: '', component: ShellComponent, canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent) },
      { path: 'pos', canActivate: [screenGuard('POS', 'Add')], loadComponent: () => import('./features/billing/pos.component').then(m => m.PosComponent) },
      { path: 'doc/:type/:id', loadComponent: () => import('./features/billing/document-editor.component').then(m => m.DocumentEditorComponent) },
      { path: 'documents/:type', loadComponent: () => import('./features/billing/document-list.component').then(m => m.DocumentListComponent) },

      { path: 'voucher/:type/:id', loadComponent: () => accounts().then(m => m.VoucherEditorComponent) },
      { path: 'vouchers', canActivate: [screenGuard('Voucher')], loadComponent: () => accounts().then(m => m.VoucherListComponent) },
      { path: 'accounts/opening', canActivate: [screenGuard('OpeningBalance')], loadComponent: () => accounts().then(m => m.OpeningBalancesComponent) },
      { path: 'accounts/cheques', canActivate: [screenGuard('Voucher')], loadComponent: () => accounts().then(m => m.ChequesComponent) },

      { path: 'masters/products', canActivate: [screenGuard('Product')], loadComponent: () => masters().then(m => m.ProductListComponent) },
      { path: 'masters/products/:id', canActivate: [screenGuard('Product')], loadComponent: () => masters().then(m => m.ProductEditorComponent) },
      { path: 'masters/parties/:type', loadComponent: () => masters().then(m => m.PartyListComponent) },
      { path: 'masters/ledger/:id', loadComponent: () => masters().then(m => m.LedgerEditorComponent) },
      { path: 'masters/account-groups', canActivate: [screenGuard('AccountGroup')], loadComponent: () => masters().then(m => m.AccountGroupsComponent) },
      { path: 'masters/employees', canActivate: [screenGuard('Employee')], loadComponent: () => masters().then(m => m.EmployeesComponent) },
      { path: 'masters/lookups/:kind', canActivate: [screenGuard('Masters')], loadComponent: () => masters().then(m => m.LookupsComponent) },
      { path: 'masters/loyalty', canActivate: [screenGuard('Loyalty')], loadComponent: () => masters().then(m => m.LoyaltyComponent) },
      { path: 'masters/offers', canActivate: [screenGuard('Offer')], loadComponent: () => masters().then(m => m.OffersComponent) },
      { path: 'masters/price-lists', canActivate: [screenGuard('PriceList')], loadComponent: () => masters().then(m => m.PriceListsComponent) },
      { path: 'masters/boms', canActivate: [screenGuard('Bom')], loadComponent: () => masters().then(m => m.BomsComponent) },
      { path: 'inventory/labels', canActivate: [screenGuard('Printing')], loadComponent: () => masters().then(m => m.LabelsComponent) },

      { path: 'reports', loadComponent: () => import('./features/reports/reports.components').then(m => m.ReportCatalogComponent) },
      { path: 'reports/:key', loadComponent: () => import('./features/reports/reports.components').then(m => m.ReportViewerComponent) },

      { path: 'settings/appearance', loadComponent: () => settings().then(m => m.AppearanceComponent) },
      { path: 'settings/company', canActivate: [screenGuard('Company')], loadComponent: () => settings().then(m => m.CompanyProfileComponent) },
      { path: 'settings/features', canActivate: [screenGuard('Settings')], loadComponent: () => settings().then(m => m.FeaturesComponent) },
      { path: 'settings/screens', canActivate: [screenGuard('Settings')], loadComponent: () => settings().then(m => m.ScreenOptionsComponent) },
      { path: 'settings/numbering', canActivate: [screenGuard('Settings')], loadComponent: () => settings().then(m => m.NumberingComponent) },
      { path: 'settings/printing', canActivate: [screenGuard('Printing')], loadComponent: () => settings().then(m => m.PrintSchemesComponent) },
      { path: 'settings/messaging', canActivate: [screenGuard('Messaging')], loadComponent: () => settings().then(m => m.MessagingComponent) },
      { path: 'settings/users', canActivate: [screenGuard('Users')], loadComponent: () => settings().then(m => m.UsersComponent) },
      { path: 'settings/roles', canActivate: [screenGuard('Roles')], loadComponent: () => settings().then(m => m.RolesComponent) },
      { path: 'settings/audit', canActivate: [screenGuard('UserLog')], loadComponent: () => settings().then(m => m.AuditComponent) },
      { path: 'settings/license', loadComponent: () => settings().then(m => m.LicenseComponent) },
      { path: 'settings/companies', canActivate: [screenGuard('Company')], loadComponent: () => settings().then(m => m.CompaniesComponent) },
      { path: 'settings/year-end', canActivate: [screenGuard('YearEnd')], loadComponent: () => settings().then(m => m.YearEndComponent) },

      { path: 'tools/import-export', canActivate: [screenGuard('Tools')], loadComponent: () => tools().then(m => m.ImportExportComponent) },
      { path: 'tools/transfer', canActivate: [screenGuard('Tools')], loadComponent: () => tools().then(m => m.TransferComponent) },
      { path: 'tools/reset', canActivate: [screenGuard('Tools')], loadComponent: () => tools().then(m => m.ResetComponent) },

      { path: 'faq/chat', loadComponent: () => faq().then(m => m.ChatbotComponent) },
      { path: 'faq/email', loadComponent: () => faq().then(m => m.EmailUsComponent) },
      { path: 'faq/manage', canActivate: [screenGuard('Faq')], loadComponent: () => faq().then(m => m.ManageFaqComponent) },
    ],
  },
  { path: '**', redirectTo: '' },
];
