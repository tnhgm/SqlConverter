# SQL Server ⇄ SQLite Converter (Type-Faithful Edition)

English | [فارسی](README.fa.md)

![.NET 10](https://img.shields.io/badge/.NET-10.0-5C2D91) ![platform](https://img.shields.io/badge/platform-Windows-blue) ![license](https://img.shields.io/badge/license-CPOL%201.02-green) ![build](https://github.com/YOUR_GITHUB_USERNAME/MSSQLToSQLite/actions/workflows/build.yml/badge.svg)

A two-way database converter between **SQL Server** and **SQLite**, with **full data-type preservation** — built on top of the classic
[MSSQLToSQLite](https://github.com/xmikedanielsx/MSSQLToSQLite) converter (originally by gusmando & contributors, 2008–2023).

This fork exists because the original converter — and every importer we tried — **silently corrupted column types** during a real-world
migration: SQL Server 2025 → SQLite → SQL Server 2022. An ASP.NET Core application then failed at runtime with
`InvalidCastException: Unable to cast object of type 'System.Int64' to type 'System.Int32'`, because `bit` had become `nvarchar`,
`datetime2` had become `nvarchar`, `int` had become `bigint`, and primary keys had disappeared entirely.

Both directions have been reworked so that a full round trip now reproduces the source schema **exactly** — verified on a production
database (32 tables, 326 columns, ~20k rows): **0 type differences, 0 data loss**.

> **خلاصه فارسی:** این نسخه، مبدل دوطرفه‌ی SQL Server ⇄ SQLite است که برخلاف نسخه‌ی اصلی، **نوع داده‌ها را هنگام تبدیل تغییر نمی‌دهد**؛
> کلیدهای اصلی (PK) روی SQL Server 2025 درست تشخیص داده می‌شوند، ستون‌های IDENTITY بازسازی می‌شوند، ایندکس‌ها برگردانده می‌شوند و
> رفت‌وبرگشت کامل با صفر اختلاف تایپ تست شده است.

---

## What's new in this edition

### Both directions

| Fix | Before | After |
|---|---|---|
| **Primary key detection** | `sp_pkeys` — returns **no rows on SQL Server 2025**, silently dropping every PK from the converted schema | `INFORMATION_SCHEMA.TABLE_CONSTRAINTS / KEY_COLUMN_USAGE` (version-proof, parameterized) |
| **Type names preserved** | `bigint→integer`, `tinyint→smallint`, `nchar→char`, `datetimeoffset→datetime` (offset values destroyed!), `decimal→numeric` (precision/scale lost) | Original SQL Server type names are kept verbatim (`bigint`, `datetime2`, `datetimeoffset`, `nvarchar(450)`, …) so the reverse direction can restore them exactly |
| **Numeric fidelity** | `numeric/decimal/money → DbType.Double` — values rounded during the copy | `DbType.Decimal` + `numeric(p,s)` declared with the exact precision/scale read from the source |
| **Missing type mappings** | `datetimeoffset`, `money`, `smallmoney`, `smalldatetime`, `varbinary`, `binary` crashed the conversion with *"Illegal DB type"* | All mapped correctly |
| **GUID handling** | left-over debug code, inconsistent mapping | clean `uniqueidentifier`/`nvarchar` mapping, `treat-guid-as-string` respected |
| **`sql_variant`, `xml`** | `sql_variant → DbType.Object` → crash | copied as text |
| **Silent failures** | CLI swallowed every exception (`exit 0`, empty output file) | progress and errors are printed to the console |

### SQLite → SQL Server (newly written direction)

- **Faithful type restoration** from the declared SQLite types (`bit → BIT`, `datetime → DATETIME2`, `nvarchar(450) → NVARCHAR(450)`, `time → TIME`, `datetimeoffset → DATETIMEOFFSET`, …), with value conversion per target type.
- **IDENTITY reconstruction**: a single-column numeric primary key is created as `IDENTITY(1,1)` (the EF/SQL Server convention), data is copied with `IDENTITY_INSERT ON`, and the seed is restored with `DBCC CHECKIDENT`.
- **FK width propagation**: identity PKs are collapsed to SQLite `integer` (a SQLite/AUTOINCREMENT limitation) while referencing columns keep `bigint`/`int`; the reverse converter walks the foreign-key graph and widens the ambiguous side until both ends of every FK agree — no more *"Column … is not the same data type as referencing column …"*.
- **Primary-key restore via sidecar**: some tables lose their PK on the SQLite side; a `"@primarykey:Table": "Col1,Col2"` entry brings it back so foreign keys resolve.
- **Index recreation** (unique indexes included), **auto-creation of the target database**, and an **empty-target guard** that refuses to import into a database that already contains tables (prevents mixed/broken schemas).
- Optional **`<file>.typemap.json` sidecar** to pin exact SQL Server types per column — e.g. when re-importing a file produced by the *old* converter, or to force `"bigint identity"` on a specific column.

### CLI

```bash
# SQL Server -> SQLite
ClassLibrary.exe --direction sql-to-sqlite --conn-type win|sql \
    --conn-server <server> --conn-db <database> --sqlite-path <file.db> \
    [--conn-user <user> --conn-pass <pass>]

# SQLite -> SQL Server
ClassLibrary.exe --direction sqlite-to-sql  --conn-type win|sql \
    --conn-server <server> --conn-db <database> --sqlite-path <file.db> \
    [--conn-user <user> --conn-pass <pass>]
```

The WinForms UI (`ApplicationForm`) offers both directions interactively.

### Sidecar file: `<sqlite-file>.typemap.json`

```json
{
  "Songs.Id": "bigint identity",
  "AspNetUsers.AccessFailedCount": "int",
  "@primarykey:Albums": "Id"
}
```

Entries are optional; anything not listed is resolved from the declared SQLite type, with a data-driven `INT`/`BIGINT` fallback
(MIN/MAX probe) for bare `INTEGER` columns. **Recommended:** generate the SQLite file with *this* edition — then no sidecar is needed
at all; the round trip is exact by itself.

---

## Verification

Round-trip test performed on a production ASP.NET Core database (32 tables / 326 columns / ~20,000 rows):

```
SQL Server (source)  ->  SQLite  ->  SQL Server (round trip)
columns:        326   =    326     =        326
type diffs:       0          -                 0
rows:        identical       -           identical
PKs:            31/31            -            31/31
```

Including: `bit`, `datetime2`, `datetimeoffset`, `time`, `int`, `bigint identity`, `nvarchar(n)`, `nvarchar(max)`.

## Requirements

- .NET 10 runtime (ClassLibrary CLI + ApplicationForm UI)
- `System.Data.SQLite` and `System.Data.SqlClient` (NuGet, restored automatically)
- The legacy .NET Framework 4.8 projects (`Converter`, `DbAccess`) are kept for reference; use the net10 `ClassLibrary`/`ApplicationForm` instead.

## Known limitations

- SQLite cannot express `AUTOINCREMENT` for non-`INTEGER` columns; identity columns therefore lose their exact width inside a
  SQLite file **only if** the file is generated by the *old* converter. Files generated by this edition carry the original type and
  round-trip exactly. For old files, use the sidecar.
- `decimal`/`money` values pass through SQLite's `NUMERIC` affinity (IEEE-754 storage); up to ~15 significant digits are preserved —
  fine for typical money amounts, but SQL Server `decimal(38,x)` with huge precision should be sanity-checked after import.
- Views and triggers are not recreated by the SQLite → SQL Server direction.

## Credits

- Original converter: gusmando — *SQL Server to SQLite Converter* (CodeProject, 2008)
- .NET 6 update & fork: [xmikedanielsx/MSSQLToSQLite](https://github.com/xmikedanielsx/MSSQLToSQLite) (2023)
- Type-faithful edition: two-way rewrite, SQL Server 2025 compatibility, IDENTITY/FK-graph restoration, CLI (2026)

Licensed under the same terms as the original project (CodeProject Open License / CPOL).
