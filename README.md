# qb-import-mgr

A Windows desktop tool that turns bank and credit-card downloads (`.qbo`, `.qfx`, `.ofx` "Web Connect" files) into IIF files for **QuickBooks Desktop 2020**. You review and categorize transactions before anything reaches QuickBooks. It also checks your QuickBooks register so nothing gets entered twice, and it learns your usual payee → account assignments from QuickBooks history.

- Imports OFX 1.x (SGML) and 2.x (XML), for bank and credit card accounts, including files with several statements.
- Catches duplicates: identical files, repeated FITIDs, and look-alike transactions from overlapping downloads.
- Categorizes with rules, payee aliases and split templates, with type-ahead category entry and bulk actions.
- Matches against your QuickBooks register so transactions already in QuickBooks are never exported again.
- Learns rules from QuickBooks history, pairs transfers between your own accounts, and holds invoiced-customer deposits until you record them with Receive Payments.
- Validates every export and backs up the database first. Exported rows are locked; a batch can be voided and redone.
- Everything is stored locally in one SQLite file. No cloud, no QuickBooks SDK.

---

## Contents

- [Requirements](#requirements)
- [Install](#install)
- [Data folder](#data-folder)
- [First-time setup](#first-time-setup)
- [Everyday workflow](#everyday-workflow)
- [Working in the main window](#working-in-the-main-window)
- [Transaction statuses](#transaction-statuses)
- [Avoiding double entry: QuickBooks register matching](#avoiding-double-entry-quickbooks-register-matching)
- [Learning from QuickBooks history](#learning-from-quickbooks-history)
- [Transfers between your accounts](#transfers-between-your-accounts)
- [Invoiced customers (Receive Payments)](#invoiced-customers-receive-payments)
- [Rules, aliases and split templates](#rules-aliases-and-split-templates)
- [Exporting to QuickBooks](#exporting-to-quickbooks)
- [Voiding an export](#voiding-an-export)
- [Duplicates](#duplicates)
- [Ledger balance check](#ledger-balance-check)
- [Backups, audit log and safety](#backups-audit-log-and-safety)
- [IIF format details](#iif-format-details)
- [Building from source](#building-from-source)
- [Project layout](#project-layout)
- [Troubleshooting](#troubleshooting)

---

## Requirements

- Windows 10 or 11, 64-bit
- QuickBooks Desktop 2020 (Pro, Premier or Enterprise) with IIF import (File > Utilities > Import > IIF Files)
- Bank and card downloads in Web Connect format (`.qbo`, `.qfx` or `.ofx`)

The published app is self-contained, so no .NET runtime needs to be installed.

## Install

1. Build `qb-import-mgr.exe` (see [Building from source](#building-from-source)) or get a copy of the built exe.
2. Put it in `C:\accounting\`.
3. Run it. On first start it creates the data folders and the database next to it.

## Data folder

Everything lives under one data folder, `C:\accounting` by default:

| Path | What it holds |
|---|---|
| `qb-import-mgr.exe` | The app |
| `qb-import-mgr.db` | The database (SQLite). Back this up with the rest of your books. |
| `qbo\` | **Inbox**: drop bank/card downloads here |
| `qbo\archive\` | Downloads are moved here after import (timestamped) |
| `iif\` | IIF files written by Export |
| `backup\` | Automatic database backups (newest 30 kept) |

To use a different folder, in order of precedence:

1. Command-line argument: `qb-import-mgr.exe --data-dir "D:\Books"`
2. Environment variable: `QBIM_DATA_DIR=D:\Books`
3. Default: `C:\accounting`. Debug builds default to `C:\accounting-dev`, so testing never touches real data.

The window title always shows which data folder is in use.

## First-time setup

1. **Back up your QuickBooks company file.** File > Back Up Company > Create Local Backup.
2. **Export your QuickBooks lists.** In QuickBooks: File > Utilities > Export > Lists to IIF Files. Tick Chart of Accounts, Customer List, Vendor List, Other Names List and Class List, and save the `.iif` file.
3. **Import the lists.** In qb-import-mgr: File > Import QuickBooks lists (IIF). This loads your accounts (used as categories), names and classes. Re-run it whenever you add accounts or names in QuickBooks. Entries that are hidden or missing from the export are marked inactive, never deleted.
4. **Import your first download.** Download a Web Connect file from your bank, save it to `C:\accounting\qbo`, and click **Import inbox**.
5. **Map the account.** On first import of a new account you're asked which QuickBooks BANK (or CCARD) account it belongs to. The dialog shows the masked account number and the bank's ID (BANKID / INTU.BID). The choice is remembered; change it later under Settings > Accounts and QuickBooks mapping.
6. **Optional, recommended:** import your QuickBooks register history and let the app learn your payees. See [Learning from QuickBooks history](#learning-from-quickbooks-history).

## Everyday workflow

1. Download new Web Connect files into `C:\accounting\qbo` and click **Import inbox** (Ctrl+I).
2. *(Recommended)* In QuickBooks, export a Transaction Detail report for the same accounts and dates, then use **File > Import QB register (CSV)**. Anything already in QuickBooks becomes **In QuickBooks** and won't be exported.
3. Work through **Needs category**: pick categories, use **Remember as rule** for repeat payees, and press Enter to approve rows.
4. Check **Possible duplicates**, **Ambiguous match** and **Waiting for Receive Payments**.
5. Click **Export** (Ctrl+E), review the per-account totals, and export.
6. In QuickBooks: File > Utilities > Import > IIF Files, and pick the new file from `C:\accounting\iif`.

## Working in the main window

**Left: accounts and views.** Each account lists its downloads by period. Accounts not yet mapped to QuickBooks show in red. The views gather rows across all accounts:

| View | Shows |
|---|---|
| Needs category | New rows with no category yet |
| Possible duplicates | Rows that look like ones from an earlier download |
| Ambiguous match | Rows that could be any of several QuickBooks entries |
| Waiting for Receive Payments | Deposits from invoiced customers |
| Ready | Approved, waiting to export |
| In QuickBooks | Matched to your QuickBooks register; never exported |
| Transfers | Both sides of transfers between your accounts |
| Excluded | Rows you (or a rule) excluded |

**Center: transactions.** Edit the payee or category directly in the grid.

| Key | Action |
|---|---|
| Typing a letter | Starts the category cell with type-ahead |
| Enter | Approve (Categorized → Ready) and move down |
| Del | Exclude |
| Ctrl+Z | Undo the last action, including "Remember as rule" |
| Ctrl+I / Ctrl+E / F5 | Import inbox / Export / Refresh |

Select several rows (Shift/Ctrl+click) to set a category, approve, exclude or create a rule for all of them at once. The toolbar's "Category for selected" box applies one category to the whole selection.

**Right: details.**

- The bank's raw values (NAME, MEMO, TRNAMT, dates, CHECKNUM, FITID). These are never changed.
- Your overrides: date, payee, memo, check number, QB name and class.
- The split editor and split templates.
- Panels for duplicates, QuickBooks matches, holds and transfers, each with its action buttons.

**Footer:** row counts by status, totals, and the [ledger balance check](#ledger-balance-check).

Window size, splitter positions and column widths are remembered.

## Transaction statuses

| Status | Meaning | Exported? |
|---|---|---|
| New | Imported, not categorized | No |
| Categorized | Has a category or balanced splits | After you approve it |
| Ready | Approved | **Yes**, on the next export |
| Exported | In an IIF batch; locked | Already |
| Excluded | You chose to leave it out | No |
| Duplicate | Looks like a row from another download | No, until resolved |
| In QB | Matched to a QuickBooks register entry | No |
| Ambiguous | Several possible QuickBooks entries | No, until resolved |
| Hold: Rcv Pmt | Invoiced-customer deposit | No |
| Transfer (in) | Receiving side of a transfer; the other side exports it | No |

## Avoiding double entry: QuickBooks register matching

Use this whenever some transactions may already be in QuickBooks: entered by hand, or downloaded with Bank Feeds.

**1. Create the report in QuickBooks Desktop**

Reports > Custom Reports > **Transaction Detail**:

- **Dates:** the period you're importing (overlapping is fine).
- **Filters:** Account = your bank and/or credit card accounts.
- **Columns:** Type, Date, Num, Name, Memo, Account, Clr, Split, Debit, Credit. A single Amount column instead of Debit/Credit also works.
- Click **Excel > Create New Worksheet > Create a comma separated values (.csv) file** and save it.

**2. Import it:** File > Import QB register (CSV).

- With an **Account** column, rows go to the bank account mapped to that QuickBooks account. Rows for accounts you haven't mapped are kept, but only used for learning.
- Without an Account column, you're asked which account the file is for.
- Report titles, group headers, subtotal and total rows are skipped. Amounts like `(123.45)`, `-123.45`, `$1,234.56` and `1,234.56-` are understood, and so are account-number prefixes such as `1000 · Checking`.
- **Re-importing is safe.** Rows already imported are recognized. For each account in the new file, rows inside that file's date range that are missing from it (changed or deleted in QuickBooks) are removed, unless they're matched. Other accounts are never touched.

**3. How matching works**

- The amount must match exactly, on the same account.
- **Check number:** same number within ±60 days. Two different check numbers never match.
- **Otherwise:** dates within ±5 days. The closest date wins, then the closest payee/name.
- **One-to-one:** each QuickBooks entry satisfies only one bank row. Two identical $5 charges with one QuickBooks entry leave one still to export.
- **Never guessed:** if two or more entries are equally good, the row becomes **Ambiguous** and you choose.
- Exported and excluded rows also claim their QuickBooks entry (without changing status), so later look-alikes aren't wrongly matched to it.
- Matching also runs after every bank import, so new downloads are checked against register rows already imported.

**4. Resolving**

- **In QuickBooks** rows show the QuickBooks type, date, number, name, memo and split. If a match is wrong, click **Not in QB**: the row goes back to New and that pairing is never suggested again.
- **Ambiguous** rows: **Pick match…** lists the candidates (best first), or click **Not in QB**.

In-QuickBooks rows still count in the ledger balance check.

## Learning from QuickBooks history

Tools > **Learn from QuickBooks** turns your QuickBooks history into rules and payee aliases, so new downloads arrive mostly categorized.

**Best input:** import a long Transaction Detail CSV (for example two years, all bank and credit card accounts) with File > Import QB register.

The app learns from two sources:

- **Payee profiles:** for each QuickBooks Name, the account it is most often assigned to (the Split column), how often, how consistently, and when it was last used. `-SPLIT-` rows are counted but never decide the account.
- **Matched pairs:** each bank row matched to a QuickBooks entry teaches *bank text → QB name + account*. The bank text is cleaned:
  - prefixes such as "POS PURCHASE", "DEBIT CARD" and "ACH" are removed;
  - store numbers, reference codes, dates and trailing city/state are dropped.

  For example, `POS PURCHASE SHELL OIL 57444 AUSTIN TX` becomes `SHELL OIL`.

**The review dialog** lists each suggestion with:

- its pattern, QB name and account;
- confidence (count and % agreement) and source (bank text or QB name);
- how many existing bank rows it would hit, plus sample bank texts.

You can:

- Click **Accept all above threshold** (default: 3+ occurrences and 90%+ agreement; both adjustable).
- Tick suggestions individually and click **Accept ticked**.
- Edit a pattern before accepting.

Each accepted suggestion becomes a **rule** (pattern → account + QB name) and a **payee alias** (clean payee = QB name), marked *Learned from QB*.

- Learned rules run after your own rules (priority 200), so **your hand-made rules always win**.
- A suggestion that disagrees with a hand-made rule shows as a **Conflict** and is never applied. One that agrees shows as *Already covered*.
- After accepting, rules re-run on every New row.

## Transfers between your accounts

When money leaves one mapped account and the same amount arrives in another within ±5 days (checking → savings, or checking → credit card payment), the two rows are **paired**:

- The **out** side is categorized to the other account's QuickBooks name and exported as a single transaction. QuickBooks sees one transfer or card payment.
- The **in** side shows *Transfer (in)* and is not exported.
- If either side is already in QuickBooks (matched or exported), neither side needs exporting.
- Ties are never guessed; such rows stay unpaired.
- **Unpair** in the detail panel breaks a wrong pair, and that pair is never suggested again.

Both accounts must be mapped to QuickBooks for pairing to happen.

## Invoiced customers (Receive Payments)

If you invoice a customer in QuickBooks, their deposits should be recorded with **Receive Payments** (against the invoice), not exported as plain deposits.

1. Tools > **QuickBooks names**: tick **Invoiced** for those customers.
2. Any deposit assigned to an invoiced customer and not yet in QuickBooks moves to **Waiting for Receive Payments**, with the note *"Record with Receive Payments in QB, then re-import register."*
3. Record the payment in QuickBooks, then re-import the register. The deposit matches and becomes **In QuickBooks**.

Held deposits are never exported. Changing the name, or unticking Invoiced, releases the hold.

## Rules, aliases and split templates

- **Remember as rule…** (toolbar, detail panel or Edit menu) proposes a rule from the selected rows. Store numbers and reference codes are dropped from the suggested pattern, and a preview shows how many of the selected rows it matches.
  - **Match on** payee, memo or either, using contains, starts-with or regex.
  - **Narrow by** direction (in/out), an amount range (absolute value) or one account.
  - **Then set** a category, QB name, class or split template, or **auto-exclude**.
  - Saving a rule applies it immediately to every New row. Ctrl+Z removes the rule and reverts those rows.
- **Payee aliases** replace the bank's payee text with a clean name and QB name. Learning creates most of them.
- **Splits:** in the detail panel, **Add line** and enter amounts in the direction of the transaction (a $1,250 check splits as 1,000 + 250). The remainder updates live, and the row can't be approved until it reads 0.00.
- **Split templates:** after saving splits, use **Save as template…** with percentages or fixed amounts; the last line always takes the remainder. **Apply** fills the split editor from a template. A rule can also apply a template automatically.

## Exporting to QuickBooks

**Export…** (Ctrl+E) takes every **Ready** row.

1. **Preview:** the count and net amount per account, plus every validation problem. Export is blocked if:
   - an account isn't mapped;
   - a category or name isn't in your QuickBooks lists (or is inactive);
   - a split doesn't balance;
   - a row is uncategorized or not Ready.

   On your first export there is also a reminder to back up the QuickBooks company file.
2. **Export:**
   - The database is backed up to `backup\`.
   - One IIF file is written to `iif\qb-import_<date>_<time>_batch<N>.iif`.
   - The rows become **Exported** and locked. All of this happens together or not at all.
3. **In QuickBooks:** File > Utilities > Import > IIF Files, then choose the file. Check a few transactions in the register.

By default imported transactions are marked cleared, so they arrive already checked for reconciliation. Turn this off under Settings > *IIF: mark exported transactions cleared*.

## Voiding an export

File > **Export batches / void…** lists every batch. **Voiding** a batch:

- returns its rows to **Ready** so you can fix and re-export them;
- renames its IIF file to `*.voided` so it can't be imported by mistake.

Voiding does **not** change QuickBooks. **Delete those transactions in QuickBooks first**, or re-exporting will post them twice. Voids are permanent and audited.

## Duplicates

| Case | What happens |
|---|---|
| Same file again (same SHA-256) | Skipped and archived; nothing added |
| Same FITID on the same account | Skipped as already imported |
| Same account, same amount, date within ±3 days, same payee, **from a different download** | Imported as **Possible duplicate** (never dropped) |

Identical charges inside one download (two $5 parking charges on the same day) are both kept. They aren't compared with each other.

- **Not a duplicate:** back to New; rules re-run.
- **Confirm duplicate:** moved to Excluded.

## Ledger balance check

Downloads overlap and don't line up with statements, so the check compares two ledger-balance snapshots, A (earlier) and B. LedgerBal(B) − LedgerBal(A) must equal the sum of that account's transactions posted after A's date and up to B's date, ignoring Excluded and Duplicate rows. The footer shows **Ledger ties ✓**, or the exact difference when it doesn't.

## Backups, audit log and safety

- **Automatic backups** before every export and before any database upgrade, in `backup\` (newest 30 kept). To restore, close the app and copy a backup over `qb-import-mgr.db`.
- **Audit log** (Tools > Audit log) records every import, edit, match, rule, export and void, with date, entity and action filters. It can't be edited or deleted.
- **Locks enforced by the database:**
  - raw bank values can never change;
  - exported rows can't be edited or deleted, except by voiding their batch;
  - voids are permanent.
- **Privacy:** full account numbers are never stored; only the last four digits (plus a one-way hash for matching). Original downloads are kept inside the database for reference.

## IIF format details

| Item | Rule |
|---|---|
| Bank, money out / in | `CHECK` / `DEPOSIT` |
| Credit card charge / credit | `CREDIT CARD` / `CCARD REFUND` |
| TRNS amount | The bank's amount (money out negative) |
| SPL amount | The negated amount; splits sum to −TRNS |
| Dates | `MM/DD/YYYY` |
| File | Windows-1252 encoding, CRLF line endings |
| Text fields | Tabs, quotes and line breaks removed |
| NAME | Only when a QuickBooks name is assigned (never invents names) |
| MEMO | Without a QB name, the bank payee goes in the memo so it isn't lost |
| CLEAR | `Y` by default (setting) |

## Building from source

Requires the **.NET 10 SDK** (Windows).

```powershell
git clone https://github.com/martymazurik/qb-import-mgr.git
cd qb-import-mgr
dotnet build
dotnet test

# Single-file, self-contained exe (about 65 MB)
dotnet publish src/QbImportMgr.App -p:PublishProfile=win-x64 -o publish
copy publish\qb-import-mgr.exe C:\accounting\
```

Close the app before replacing the exe. Debug builds (`dotnet run --project src/QbImportMgr.App`) use `C:\accounting-dev` unless you pass `--data-dir`.

## Project layout

```
src/
  QbImportMgr.Core   OFX parser, rules engine, duplicate detection, QB register CSV reader,
                     matcher, transfer pairing, payee learning, split templates, IIF writer,
                     export validation (no UI or database code)
  QbImportMgr.Data   SQLite schema and migrations, repositories, services (import, export,
                     void, matching, learning, backups)
  QbImportMgr.App    WPF app (MVVM with CommunityToolkit.Mvvm)
tests/
  QbImportMgr.Tests  xUnit tests with synthetic bank, Amex, OFX 2.x, multi-statement,
                     QB list and QB register samples
```

Stack: C# / .NET 10, WPF, Microsoft.Data.Sqlite + Dapper, xUnit.

## Troubleshooting

| Problem | Fix |
|---|---|
| A download "FAILED — left in inbox" | It isn't a valid OFX/QFX/QBO file. Re-download it from the bank as a Web Connect / QuickBooks file. |
| Amounts have the wrong sign | Settings > Accounts: tick **Invert sign** for that account. This is blocked once rows are exported; void those batches first. |
| Export says a category isn't in the chart of accounts | Re-export your QuickBooks lists and re-import them. Make sure the account isn't inactive in QuickBooks. |
| Register import: "no header row" | The report needs Type, Date and Amount (or Debit/Credit) columns. Save it as CSV from the report window. |
| Register rows aren't matching | Map the bank account to the same QuickBooks account the report uses, then re-import the CSV. |
| Something was matched wrongly | **Not in QB** (register match) or **Unpair** (transfer). Either is remembered. |
| Need to check what happened | Tools > Audit log |
