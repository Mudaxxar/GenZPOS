# GenZ-POS — Blazor Hybrid Desktop App

Product name **GenZ-POS** (renamed from "Paint Shop" on 2026-10-16). Solution, project folders, exe, install folder,
logs and backup paths use `GenZ-POS`; C# namespaces use `GenZPOS` (a namespace can't contain `-`). The database is still
named `Blazor`. The shop sells paint, so "paint shop" in the business rules below describes the customer, not the app.

## Owner's standing decisions (always follow these)

- **Platform:** .NET 10 Blazor Hybrid **desktop** app (WPF host + `BlazorWebView`, Windows only).
  MAUI workload is not installed; WPF hosting was chosen to avoid it.
- **UI:** Radzen Blazor (`Radzen.Blazor`) for all UI components — grids, forms, dialogs,
  notifications, layout. Don't pull in other UI libraries or hand-roll components Radzen already has.
- **Database:** local SQL Server 2016 (Express), server `.`, Windows auth (no password),
  database name **`Blazor`**.
  Connection string: `Server=.;Database=Blazor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True`
- **Data access:**
  - **EF Core is the default for everything:** CRUD, forms, transactions, migrations, seeding.
  - **Dapper is only for the few heavy report/search queries** where EF's generated SQL isn't good
    enough (customer ledger, stock report, dashboard aggregates).
  - Dapper **shares EF's connection**: create the `DbContext` from `IDbContextFactory<AppDbContext>`
    and run Dapper on `db.Database.GetDbConnection()`. Never open a separate connection string for Dapper.
  - SQL Server 2016 = compatibility level 130 → `UseCompatibilityLevel(130)` is set so EF doesn't
    emit SQL that 2016 can't run.
- **DbContext lifetime:** always `IDbContextFactory<AppDbContext>` with a **short-lived context per
  operation** (`await using var db = await DbFactory.CreateDbContextAsync();`). Never inject
  `AppDbContext` directly or keep one alive for a page/app lifetime.
- **Big lists:** never render thousands of rows. Use `RadzenDataGrid` server-side paging (`LoadData` +
  `Count`) for growing tables (products, sales, purchases, payments, stock movements), or `<Virtualize>`.
- **Async everywhere:** every DB call is awaited (`ToListAsync`, `SaveChangesAsync`, Dapper `QueryAsync`)
  so the UI never freezes.
- **WPF is only the window.** The whole UI is Blazor. Don't place WPF controls over the WebView.
- **Enter-key data entry (owner requirement):** in every form Enter moves to the next field instead of Tab;
  last field → focuses the Save/Add button → Enter clicks it → focus returns to the first field.
  Implemented once in `wwwroot/js/enter-nav.js` (DOM focus is the one place JS is the right tool).
  It applies inside `<form>` (RadzenTemplateForm), dialogs and any `data-enter-group="true"` container.
  Markers: `data-enter-submit="true"` (main button of a non-form group), `data-enter-first="true"` (where
  focus returns after Add), `data-enter-skip="true"` (keep native Enter, e.g. barcode box).
  Data-entry dropdowns use `OpenOnFocus="true"` so arriving by Enter opens the list → type to filter → Enter picks.
  **New forms/pages must follow this.**
- **Native features from C#, not JS:** file dialogs, printing etc. go through C# services
  (`Services/DesktopService.cs`) that call WPF/Win32/WebView2 APIs directly. Don't add JS interop for them.
- **Scope:** keep it simple. No double-entry accounting, no chart of accounts, no tax engine.
- **Login, roles & rights (no IdentityServer / OAuth — one desktop app, one database):**
  - Tables `Users` (one role each), `Roles`, `RolePermissions`. Passwords hashed with `PasswordHasher<AppUser>`
    (Microsoft.Extensions.Identity.Core). Seeded roles Admin (Id 1, `IsAdmin` = every right, not editable), Manager,
    Cashier. First start creates `admin`/`admin` with `MustChangePassword` (`AuthService.EnsureAdminAsync`).
  - Rights are strings in `Services/Permissions.cs` (also the policy names). **Never rename one** (stored in the DB);
    add a new one to `Permissions.Groups` and, if existing roles need it, an EF migration adding RolePermissions rows.
  - `CurrentUser` (singleton) holds the session; `AppAuthStateProvider` exposes it to Blazor as claims; `Routes.razor`
    shows `LoginScreen` until someone logs in, then the forced password change, then `AuthorizeRouteView`.
  - **Every new page** gets `@attribute [Authorize(Policy = Permissions.X)]`; menu items (`MainLayout`) get
    `Visible="@Can(...)"`; buttons for rights the page doesn't imply use `Visible="@Can(Permissions.X)"` (PageBase).
  - **Services enforce rights too:** document writes/deletes call `currentUser.Demand(Permissions.X)` first (throws a
    `BusinessException`). Tests pass `CurrentUser.ForSystem()`.
  - Rules: 5 wrong passwords lock the user 5 minutes; at least one active Admin must remain; you can't delete or switch
    off yourself. Logins, password and role changes are written to the log.
  - Limit: SQL Server uses Windows auth, so anyone with SQL tools on that Windows account can bypass the app login.

## What the app does (paint shop)

Shops buy paint from suppliers, keep stock by brand / category / pack size, and sell to walk-in
customers and to painters/contractors who often buy on credit.

| Area | Notes |
|---|---|
| Companies | Paint brands (Berger, Nippon, Dulux, Diamond, Brighto, …) |
| Categories → Sub-categories | e.g. Emulsion → Interior / Exterior; Enamel; Primer; Putty; Wood Finish; Accessories |
| Products | **Owner's model (2026-10-19): `Product` = master only** — brand + sub-category + name + pack size (Quarter / Gallon / Drum …) + unit + max discount % + active (e.g. "Zee Shoes · Shoes"). **What is bought, sold and stocked is a `StockItem` (table `StockItems`)**: article no. + colour (`Colour`, the paint shade) + size (brush 2", shoe 42 — separate from pack size), with its own unique barcode, purchase price, sale price, reorder level, active and stock. One product can have several articles (ART001, ART002 … all "Shoes"); an article no. belongs to one product; within an article each colour + size is there once. **Sale / purchase / return lines and stock movements point at the stock item (`StockItemId`)**; `ProductLookup.Id` / till pickers / barcode box are stock item ids (`ProductLookup.ProductId` = the product). Item sales "by product" group by product. Article no. is searchable on the Products page, in item pickers and in the till's barcode box |
| Max discount % | `Product.MaxDiscountPercent` (null = no limit, 0 = no discount). One rule in `Services/DiscountRules.cs`, used by the till (hints) and enforced in `SaleService.CreateAsync`: a line's rate ≥ sale price × (1 − max %), and the invoice discount ≤ Σ each line's remaining room (lines without a limit add their whole amount). No override, not even for Admin. The till disables Save while over the limit (`DiscountProblem`) |
| Rs / % discount | Invoice (sale) and bill (purchase) discount use `<DiscountInput>` (Shared): typed as Rs or % of the sub total (% follows the cart), always saved as Rs (`DiscountRules.Amount`). Shows the items' max in Rs and %, with "use max" (`DiscountRules.MaxPercent` rounds down so it never lands over) |
| Stock items (articles, sizes & colours) | All writes go through `Services/StockItemService.cs`: `SaveAsync(product, edited items, new StockItemToCreate[], StockItemPrices, articleNo = null)` (product dialog; new items go under `articleNo`, null = the next ART…; combinations that article already has are skipped; duplicates refused), `EnsureOneAsync(productId, articleNo (null = new article), colour, size, barcode, prices)` (purchase panel; purchase right is enough), `GetArticlesAsync(productId)`, `DeleteStockItemAsync` / `DeleteProductAsync` (only never-used ones). `ProductDialog` (owner simplified it — don't re-add without asking): company, category, sub-category, name, pack, unit, active only — **no article no., no max discount %, no sizes & colours sections**. A new product also shows purchase / sale price, low-stock level, barcode (auto, Read-only checkbox to type/scan) and opening stock, and gets one plain stock item with a new ART… no. Products list = one row per product (its article nos., sizes & colours summary, price ranges, total stock). **New Purchase: the Item list is products (`ReportService.GetProductPicksAsync`, from the Products table)** → **Article no. (read-only, automatic: the next ART… no. reserved when the product is picked; picking the same product again on that purchase keeps it)** → Cost price ↔ Profit % ↔ Retail (prefilled from the product's latest stock item) → "+" (only expands / collapses, never a pop-up; no item: asks to pick one — new products are made on the Products page). Picking a product opens `PurchaseArticleDialog` INLINE for that article no. (owner's order): Barcode (read-only: the stock item's own, or the next SH… reserved for a new one) → Colour → Size → Qty → Add = one line in the table, panel stays open. Pricing: `Services/Pricing.cs` (profit % on cost). `PurchaseLine.SalePrice` = retail set on the purchase; `PurchaseService.ApplyRetailPricesAsync` makes it the stock item's sale price on save (null = unchanged). Scanning / typing an article no. in `BarcodeScanBox` opens `VariantPickerDialog` (colour × size grid with stock) |
| Article no. / barcode codes | `Services/ProductCodes.cs` — every creation path (product dialog, purchase panel, Excel import) goes through it. Barcode: unique per **stock item** (required, unique index); empty → `SH000001`… from SQL sequence `BarcodeSeq` (print as Code 128). Article no.: on the **stock item** (required, indexed, not unique — an article's colours / sizes share it); `ResolveArticleNoAsync(db, given, productId)` refuses one that another product's items have; empty → always a NEW no. (never guessed by name), `ART000001`… from `ArticleNoSeq`. Sequences, never max+1 (two counters at once). Excel: a blank code cell keeps the existing code; new rows get codes in `ImportAsync` |
| Suppliers | Who stock is purchased from |
| Purchases | Supplier bill with lines → **stock in**; updates product's last purchase price |
| Sales (invoices) | Customer invoice with lines, discount, amount paid → **stock out**; unpaid part goes to the customer ledger. Printable invoice. **No tint/shade code per line** — owner removed it as confusing (`SaleLine.ShadeCode` column kept but unused; don't re-add to the UI unless asked) |
| Customer payments | Money received later against credit |
| Customer ledger | Opening balance + sales (debit) − payments (credit), running balance (Dapper) |
| Stock adjustments | Manual in/out: opening stock, damage, returns, count corrections |
| Stock report | On-hand qty, stock value, low-stock flag (Dapper) |
| Products Excel import/export | `Services/ProductExcelService.cs` (ClosedXML, .xlsx only). **One row = one stock item**; the "Id" column is the stock item id; "Article no" and "Shade" (colour) columns are the stock item's. Product columns (Company, Category, Sub-category, Product, Pack size, Unit, Max discount %) must agree on every row of one product (else a row error); one article no. = one product. Export = all stock items + Help + Lists sheets; the same file imports back. Row match: Id → barcode → product by article no. (the row's company / name / pack must be that product's; a rename needs Id or barcode) or by company + name + pack, then its stock item of that article no. (or any, when blank) + colour + size, case-insensitive, else new (new rows without article no. → one new ART… per product in the file). Blank numbers/Active keep current values. Preview first (`ProductImportDialog`), all-or-nothing, never deletes. "Opening stock" column → an Opening `StockMovement` (same transaction) only for new variants or ones with no stock movements yet; others are listed in `OpeningSkipped`, so a re-imported file never doubles stock. On an expanded article row the qty is per colour / size. Exported empty. Needs `Catalog.Manage`. Column titles are matched by name (aliases in `HeaderAliases`) — keep the export headers stable. Articles: a row WITH an article no. whose Shade / Size holds a list or range ("Blue, Orange" / "40-45") expands to every colour × size (`ProductExcelService.Expand`; Id and Barcode must be empty there; rows without article no. are never split). Export keeps an article's sizes in size order and adds "In stock (not imported)" + an Articles summary sheet (not imported) |

### Money rules (single source of truth — every balance query, ledger and report must match these)

Balances are always computed from source rows (never stored):

- **Customer balance (they owe us)** = `Customer.OpeningBalance`
  + Σ Sales (`Total − Paid`) − Σ CustomerPayments.`Amount` − Σ SaleReturns (`Total − Refunded`).
  Negative = advance (we owe them).
- **Supplier balance (we owe them)** = `Supplier.OpeningBalance`
  + Σ Purchases (`Total − Paid`) − Σ SupplierPayments.`Amount` − Σ PurchaseReturns (`Total − Received`).
- **Customer ledger rows** (Debit = owes more, Credit = owes less): Invoice `Total` (Dr); Paid at counter `Paid` (Cr);
  Payment `Amount` (Cr); Return `Total` (Cr); Refund `Refunded` (Dr). Ordered by Date, CreatedAt, Seq.
- **Supplier ledger rows** (Debit = we owe more): Bill `Total` (Dr); Paid on bill `Paid` (Cr); Payment `Amount` (Cr);
  Return `Total` (Cr); Cash received back `Received` (Dr).
- **Stock**: per stock item (`StockMovement.StockItemId`); on-hand = Σ StockMovements (QtyIn − QtyOut). Types: Opening, Purchase(in, RefId=Purchase), Sale(out,
  RefId=Sale), AdjustIn, AdjustOut, SaleReturn(in, RefId=SaleReturn), PurchaseReturn(out, RefId=PurchaseReturn).
  Every document writes its movements in the same transaction; deleting a document deletes its movements.
- **Cost/profit**: `SaleLine.Cost` = the stock item's `PurchasePrice` at sale time (set in SaleService, never from UI).
  `SaleReturnLine.Cost` copied from the original sale line. COGS = Σ SaleLine Qty×Cost − Σ SaleReturnLine Qty×Cost.
  Net sales = Σ Sale.Total − Σ SaleReturn.Total. Gross profit = Net sales − COGS.
  `StockItem.PurchasePrice` = cost on the latest remaining purchase line (recomputed after purchase create/edit/delete).
- **Cash (Day Book)**: counter money and `PaymentMethod.Cash` payments are cash; Bank/Cheque/Other are non-cash.
  Cash in = Sale.Paid + CustomerPayments(Cash) + PurchaseReturn.Received; Cash out = Purchase.Paid +
  SupplierPayments(Cash) + SaleReturn.Refunded.
- **Returns**: a sale return is normally against an invoice (`SaleId`): per product, returned qty ≤ sold qty on that
  invoice − earlier returns; rate = invoice rate. Walk-in returns must be refunded in full. A sale/purchase that has
  returns can't be deleted (delete the return first). Same idea for purchase returns (`PurchaseId`, qty ≤ bought − returned,
  and ≤ on-hand stock).
- **Shop details** (name/phone/address/invoice footer) live in the `Settings` table, read via the `ShopInfo` singleton.

### Business rules
- Stock lives in one table, `StockMovements` (QtyIn / QtyOut + type + reference id). On-hand = Σin − Σout.
  Purchases, sales and adjustments write movements in the **same EF transaction** as the document.
- Selling more than on-hand stock is blocked.
- The seeded **Walk-in Customer** (Id 1) can't buy on credit: invoice must be fully paid.
- Deleting a sale or purchase also deletes its stock movements. Purchases can be **edited** (qty can't go below what's
  already sold). Sale invoices are not edited: same-day mistakes → delete & re-enter; later → Sales Return.
- **Local dev note:** from Codex's tool shell, SQL Windows auth over shared memory fails ("untrusted domain"); use
  named pipes: `sqlcmd -S "np:\\.\pipe\sql\query" -E -d Blazor ...` and
  `dotnet ef database update --project GenZ-POS --connection "Server=np:\\.\pipe\sql\query;Database=Blazor;Trusted_Connection=True;TrustServerCertificate=True"`.
- Money is `decimal(18,2)`, quantities `decimal(18,2)`. Currency shown as "Rs".

## Project layout

```
GenZ-POS.slnx
GenZ-POS/                  WPF + Razor SDK project (net10.0-windows)
  App.xaml / MainWindow.xaml   host window, DI setup
  wwwroot/index.html           Radzen theme css + js
  Data/AppDbContext.cs         EF model, seed data, design-time factory
  Data/Entities/               entity classes
  Data/Migrations/             EF migrations
  Services/                    transactional services (EF) + ReportService (Dapper)
  Components/                  Routes, MainLayout, Pages/*.razor, shared dialogs
```

## UI conventions (follow on every page)

- **Server-paged list pages** (`RadzenDataGrid` with `LoadData`): the grid's data field must start as **`null`**
  (`private List<T>? rows;`) and `loading = true`. Radzen 12 only auto-calls `LoadData` after first render when
  `Data == null`; starting with `[]` makes the screen open empty until a filter is touched (this was a real bug).
  Wrap the load in try/catch/finally with a `loadVersion` counter so stale results are ignored and errors show
  `Toast(..., FriendlyMessage(ex))`. Reference implementation: `Pages/PurchasesList.razor`.
- **Dates:** never use a bare `RadzenDatePicker`. Use `<DateBox Label="Date" @bind-Value="x" />` (types like
  WinForms: `14`, `1410`, `14/10/26`, `t`, `y`, `+3`) and `<DateRangeBar @bind-From @bind-To Changed="..." />`
  (Today / This week / This month … presets) on lists.
- **Payment status:** `<PayStatusBadge Total Paid />` (Paid / Partial / Unpaid chip) and the `PayStatusFilter` enum
  for "Pending payment / Partly paid / Not paid / Fully paid" filters.
- **Errors:** services throw `BusinessException` for user-facing rule messages; `PageBase.RunAsync` / `FriendlyMessage`
  turn anything else into a friendly message. `MainLayout` has an `ErrorBoundary` per page.
- **Text inputs** bound to entity fields set `MaxLength` matching the entity's `[MaxLength]`.
- **Money in forms:** `RadzenNumeric ... Format="N2"`; display with `Money(x)`, dates with `Date(x)`.
- **Till screen (`Pages/SaleNew.razor` + `SaleNew.razor.css`):** full-height grid (cart left, checkout panel right),
  big total display, label/value rows. Page-specific look lives in the scoped `.razor.css` (reach Radzen internals
  with `::deep`); styles for Radzen popups (dropdown item templates) must go in `wwwroot/css/app.css` because popups
  render outside the page's CSS scope.
- **F-key shortcuts:** `data-hotkey="F9"` on a button (clicked) or field (focused), handled by `wwwroot/js/hotkeys.js`.
  Sale screen: F2 item, F4 customer, F8 paid, F9 save & print; invoice view: F2 new sale. Show them as `<kbd>`.
- **Quick-add:** where a form needs a master (company, supplier, product…), put a `+` button next to the dropdown
  (see `ProductDialog`, `PurchaseEditor`, `NamePromptDialog`).
- **Barcode scanning:** use `<BarcodeScanBox Products="@products" Found="..." />` (Shared) — never a hand-rolled box.
  It looks up barcode, then article no. (`ProductLookup.MatchCode`), warns on no / several matches, clears itself and
  keeps focus (`data-enter-skip`) so the next item can be scanned. `AutoFocus` = cursor there when the page opens;
  `EnterFirst` = focus comes back to it after the Enter group's Add (enter-nav.js); `FocusAsync()` for code. Used on
  Item History (opens that product's card) and New Sale. Not inside a `<form>`: Enter would submit it.
- **New Sale entry strip (owner: like New Purchase), two rows inside the cart column:** Barcode (focused when the screen
  opens) | Item (F2: `RadzenAutoComplete` type-ahead over stock items — barcode, article no., name, company, colour,
  size; exact code first; typed code + Enter picks it) / Article no. · Colour · Size (read-only, from the stock item) →
  Qty → Rate → Add → back to Barcode. A scan fills the
  strip and moves to Qty (it does not add straight to the cart). Cart lines show Barcode (article · colour · size under it)
  then the item.

## Gotchas

- **Migrations:** run `dotnet ef database update` *without* `--no-build` right after `migrations add`, otherwise it
  applies against the stale assembly and fails with `PendingModelChangesWarning`.
- **`StockItems` migration (2026-10-19)** renamed `ProductVariants` → `StockItems` and `VariantId` → `StockItemId` (same rows / Ids) and moved `ArticleNo` from `Products` onto the stock items. Before it, the **`ProductVariants` migration (2026-10-19)** turned each old product row into a variant WITH THE SAME ID (lines and
  movements only had `ProductId` renamed to `VariantId`); rows sharing an article no. became one product, rows without
  one were grouped by company + name + pack. Both `Down`s throw: restore the pre-upgrade backup instead.

- **WebView2 composition crash:** .NET 10's WPF `BlazorWebView` always uses `WebView2CompositionControl`. With the
  old transitive WebView2 SDK (1.0.3179) the app fail-fast crashed in `CoreMessaging.dll` (0xc0000602, exit code 2,
  visible only in Event Viewer). Fix: `Microsoft.Web.WebView2` + `Microsoft.AspNetCore.Components.WebView` pinned to
  current versions in the csproj, and `MainWindow.OnClosing` disposes the BlazorWebView before the window closes.
  If exit code 2 shows up again, check Event Viewer → Application → "Application Error" for GenZ-POS.exe.
- **Never edit source files with Windows PowerShell 5.1 `Get-Content -Raw | Set-Content`.** It reads
  BOM-less UTF-8 as ANSI and double-encodes non-ASCII chars (`›` became `â€º` in the UI). Use the
  Edit/Write tools, or `[IO.File]::ReadAllText/WriteAllText` with an explicit UTF-8 encoding.

- **Target framework must be versioned:** `net10.0-windows10.0.19041.0`, not plain `net10.0-windows`.
  The .NET 10 WPF `BlazorWebView` uses WebView2's composition control, which needs
  `Microsoft.Windows.SDK.NET`; without it the app crashes at startup with `FileNotFoundException`.

- **WPF `_wpftmp` build pass vs Razor:** WPF compiles a temporary `*_wpftmp.csproj` that also runs the
  Razor generator. A plain string passed to a `RenderFragment` parameter (e.g. `Helper="some text"` on
  `RadzenFormField`) makes that pass emit broken code ("compilation unit cannot directly contain members",
  `Parameter` not found). Use `Text`/placeholder instead, or pass the fragment as a child element.
- Inline `Data="@Enum.GetValues<T>()"` is a Razor error → wrap it: `Data="@(Enum.GetValues<T>())"`.
- `System.IO` isn't imported in Razor files; write `System.IO.Path`.
- Radzen v12: `RadzenComponents` (dialogs/notifications/tooltips) lives in `MainLayout`; theme CSS is linked
  in `wwwroot/index.html` (`_content/Radzen.Blazor/css/material.css`).

## Production / deployment

- **Config:** `appsettings.json` next to the exe (`AppConfig.cs`): ConnectionString, BackupFolder (default
  `C:\GenZ-POS-Backups`), KeepBackups, AutoBackupDaily. `AppDbContext.DefaultConnectionString` is only the fallback +
  design-time value. When updating a shop PC never overwrite its `appsettings.json`.
- **Startup** (`App.xaml.cs`): fixed culture en-GB + "Rs" → single instance (named mutex) → WebView2 check → config →
  splash → SQL connect retry → pre-upgrade backup if migrations pending → Migrate → ShopInfo/Backup load → window →
  background warm-up + daily auto-backup.
- **Logs:** `%LocalAppData%\GenZ-POS\logs` (`AppLog.cs`, 60 days). `PageBase.FriendlyMessage` logs any
  non-`BusinessException` automatically.
- **Backups:** `Services/BackupService.cs` (`BACKUP DATABASE` by SQL Server itself → the SQL service account needs write
  access to the folder; `deploy\setup.cmd` grants it). Settings & Backup page: `/settings`.
- **Release builds** disable WebView2 browser keys (F5/Ctrl+R/Ctrl+P…) — features must not rely on them; page hotkeys
  (F2/F4/F8/F9) still work.
- **Publish:** `powershell -ExecutionPolicy Bypass -File deploy\publish.ps1` → `dist\GenZ-POS` (self-contained win-x64,
  ReadyToRun, with setup.cmd / restore.cmd / README-INSTALL.md). Never ship a copied `bin` folder (static web assets
  point to dev paths there).
- **Reports routes:** `/reports/daybook`, `/reports/sales-profit`, `/reports/item-sales`, `/reports/receivables`,
  `/reports/payables`, `/stock/{id}/history`. Returns: `/sales/{id}/return`, `/sale-returns`, `/purchases/{id}/return`,
  `/purchase-returns`.

## Tests

`GenZ-POS.Tests/ShopWeekScenarioTests.cs` runs a full shop week (purchases, credit/walk-in sales, both returns,
payments, purchase edit/delete, stock count) against a throw-away `GenZPOS_Test` database and checks every
balance, ledger, report and dashboard figure against hand-calculated values. Run `dotnet test GenZ-POS.Tests`
after any change to services or report SQL. (Backend only — the owner tests the UI by hand.)
`GenZ-POS.Tests/AuthTests.cs` covers login, lockout, password change, last-admin rules and service-level rights
(database `GenZPOS_AuthTest`). Without a default SQL instance (e.g. LocalDB only), set
`TEST_DB_CONNECTION=Server=(localdb)\MSSQLLocalDB;Database=GenZPOS_Test;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True`.

## Commands

```powershell
dotnet build GenZ-POS.slnx
dotnet run --project GenZ-POS
# migrations (dotnet-ef local tool; design-time factory in Data/AppDbContext.cs)
dotnet ef migrations add <Name> --project GenZ-POS --output-dir Data/Migrations
dotnet ef database update --project GenZ-POS
```
The app also calls `Database.Migrate()` at startup, so a fresh machine just needs SQL Server running.
