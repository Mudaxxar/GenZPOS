# GenZ-POS: installation and care guide

This guide is for the shop owner. Follow the steps in order. If something doesn't match what you see, stop and call your technician.

---

## What you need

| | |
|---|---|
| Computer | Windows 10 or Windows 11 (64-bit) |
| Main computer | **SQL Server Express** (free from Microsoft). This is where all the shop's data is kept. |
| Every computer | **Microsoft Edge WebView2 Runtime** (free). Windows 11 already has it. GenZ-POS checks for it and opens the download page if it's missing. |
| The program | The **GenZ-POS** folder you were given (USB drive or zip file) |

You do **not** need to install .NET or anything else. The program folder already contains everything it needs.

---

## A. Install on the main computer

### Step 1: Install SQL Server Express (skip this if it's already installed)

1. Download **SQL Server Express** from Microsoft (search for "SQL Server Express download").
2. Run it and choose **Basic**, then accept the defaults.
3. When it finishes, the last screen shows a **connection string** or **instance name**:
   - If it says `MSSQLSERVER` or `localhost`, there's nothing more to do.
   - If it says `SQLEXPRESS` (for example `localhost\SQLEXPRESS`), write it down. You'll need it in Step 3.

### Step 2: Copy the program

1. Copy the whole **GenZ-POS** folder to `C:\` so you end up with `C:\GenZ-POS\GenZ-POS.exe`.

### Step 3: Only if your SQL Server is called SQLEXPRESS

1. Open `C:\GenZ-POS\appsettings.json` with **Notepad**.
2. Change `Server=.;` to `Server=.\\SQLEXPRESS;`. Type the backslash **twice**, exactly as shown.
3. Save and close Notepad.

### Step 4: Run the setup (once)

1. Open `C:\GenZ-POS`.
2. Right-click **setup.cmd** and choose **Run as administrator**, then click **Yes**.
3. Wait for **Done.** and press any key.

Setup does four things:

- creates the backup folder `C:\GenZ-POS-Backups`
- allows SQL Server to save backups into that folder
- makes SQL Server start automatically with Windows
- puts a **GenZ-POS** icon on the desktop

### Step 5: First start

1. Double-click **GenZ-POS** on the desktop.
2. The first start takes about a minute because it creates the database. Later starts are quicker.
3. Log in with user **admin** and password **admin**. You must then choose your own password. Keep it safe: it is the owner's (Admin) password.
4. Open **Users & Roles** and add a user for each person who works at the counter. Give each one a role:
   - **Admin**: everything (users, settings, backup, deletes).
   - **Manager**: everything except users and settings.
   - **Cashier**: new sales, sales list, customers, receiving payments and the stock report. No deletes, no profit or cash reports.

   You can change what Manager and Cashier may do, or add your own roles, on the **Roles & rights** tab.
   If someone forgets their password, edit their user, type a new password and keep "must choose own password" ticked.
5. Open **Settings & Backup** (bottom of the menu) and fill in your **shop name, phone, address and invoice footer**, then press **Save**. These details appear on invoices.

Only one copy of GenZ-POS can run at a time. If you double-click the icon again, the open window comes to the front.

---

## B. Backups: the most important part

- **Automatic:** once a day, the first time GenZ-POS opens on the main computer, it saves a backup into `C:\GenZ-POS-Backups`. It keeps the newest **30** backups and deletes older ones.
- **By hand:** **Settings & Backup** → **Backup now**.
- **Keep a copy away from the shop computer (do this every week!):** plug in a USB drive, go to **Settings & Backup** → **Copy latest backup to…**, and choose the USB drive. If the computer breaks or is stolen, that USB copy is your shop's data.
- If a backup fails, GenZ-POS shows a message, and **Settings & Backup** shows the reason in red. The usual fix: run **setup.cmd** as administrator again.

Backup file names look like `Blazor_20261014_093000.bak` (database name, then date and time).
Files ending in `_pre-upgrade` are made automatically before a new version updates the database.

---

## C. Restore a backup (bring back old data)

Restoring **replaces all current data** with the data in the backup. Anything entered after that backup was made is lost.

1. Close GenZ-POS on **every** computer.
2. On the **main computer**, open `C:\GenZ-POS`, right-click **restore.cmd** and choose **Run as administrator**.
3. Drag the `.bak` file (from `C:\GenZ-POS-Backups` or your USB drive) into the black window and press **Enter**.
4. Type `YES` and press **Enter**.
5. When it says **Done**, start GenZ-POS again.

Before it restores, restore.cmd saves a copy of the current data (`…_before-restore.bak`), so a mistake can be undone.

**New computer after a breakdown:** do steps A1 to A4 first, then this restore, then start GenZ-POS.

---

## D. Install a new version of GenZ-POS

1. Close GenZ-POS on **every** computer.
2. Rename `C:\GenZ-POS` to `C:\GenZ-POS-old`.
3. Copy the new **GenZ-POS** folder to `C:\`.
4. Copy **appsettings.json** from `C:\GenZ-POS-old` into `C:\GenZ-POS` and choose **Replace**. This keeps your settings.
5. Start GenZ-POS on the main computer first. If the new version needs to update the database, it saves a `_pre-upgrade` backup first and then updates the database.
6. When everything works, you can delete `C:\GenZ-POS-old`.

The version number appears in the window title and at the bottom of the menu.

---

## E. A second computer (optional; ask your technician)

The second computer uses the **main computer's** data over the shop network. The steps are technical.

On the **main computer**:

1. In *SQL Server Configuration Manager*, open **SQL Server Network Configuration**, enable **TCP/IP**, and restart SQL Server. For a `SQLEXPRESS` instance, also start the **SQL Server Browser** service and set it to Automatic.
2. Allow SQL Server through Windows Firewall: TCP port 1433, plus UDP 1434 if SQL Server Browser is used.
3. Give the second computer's user access to SQL Server, using one of these:
   - **Windows login:** create the same Windows user name **and** password on both computers. Then add that user as a SQL login (`MAINPC\username`) with access to the `Blazor` database (db_owner).
   - **SQL login:** turn on "SQL Server and Windows Authentication mode", create a login such as `paintshop` with a strong password and db_owner on `Blazor`, and use the connection string below.

On the **second computer**:

1. Install WebView2 if GenZ-POS asks for it.
2. Copy the GenZ-POS folder to `C:\GenZ-POS` and run **setup.cmd** as administrator (for the desktop icon).
3. Edit `C:\GenZ-POS\appsettings.json` with Notepad and set the server to the main computer's name, for example:
   - Windows login: `"ConnectionString": "Server=MAINPC;Database=Blazor;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"`
   - SQL login: `"ConnectionString": "Server=MAINPC;Database=Blazor;User Id=paintshop;Password=YOUR-PASSWORD;TrustServerCertificate=True;MultipleActiveResultSets=True"`
   - For SQLEXPRESS, write `MAINPC\\SQLEXPRESS` with two backslashes.

The second computer doesn't make backups. Backups are always made on the main computer, and the main computer must be switched on whenever the second one is used.

---

## F. Settings file (appsettings.json)

| Setting | What it does | Default |
|---|---|---|
| `ConnectionString` | Which SQL Server and database to use | `Server=.;Database=Blazor;…` |
| `BackupFolder` | Where backups are saved (on the main computer) | `C:\GenZ-POS-Backups` |
| `KeepBackups` | How many backups to keep | `30` |
| `AutoBackupDaily` | Automatic daily backup on or off (`true` / `false`) | `true` |

If you change `BackupFolder`, run **setup.cmd** as administrator again so SQL Server is allowed to write there.
If the file gets damaged, GenZ-POS warns you and uses the defaults.

---

## G. When something goes wrong

| Message | What to do |
|---|---|
| "can't reach the database server" | If the computer was just switched on, wait a minute and open GenZ-POS again. Otherwise restart the main computer. |
| "needs the Microsoft Edge WebView2 Runtime" | Install it from the page that opens, then start GenZ-POS again. |
| "SQL Server is not allowed to write into the backup folder" | Right-click **setup.cmd** and choose **Run as administrator**. |
| "SQL Server refused this Windows user" | This Windows user has no access to SQL Server. Call your technician. |
| Anything else | Go to **Settings & Backup** → **Open logs folder** and send today's log file (for example `2026-10-14.log`) to your technician. |

Log files are kept for 60 days in `%LocalAppData%\GenZ-POS\logs`. To open that folder, paste the path into the Explorer address bar.
