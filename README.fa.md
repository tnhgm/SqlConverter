# مبدل SQL Server ⇄ SQLite (نسخه‌ی حفظ تایپ‌ها)

[English](README.MD) | فارسی

![.NET 10](https://img.shields.io/badge/.NET-10.0-5C2D91) ![platform](https://img.shields.io/badge/platform-Windows-blue) ![license](https://img.shields.io/badge/license-CPOL%201.02-green)

مبدل دوطرفه‌ی **SQL Server** و **SQLite** با **حفظ کامل نوع داده‌ها** — بر پایه‌ی مبدل کلاسیک
[MSSQLToSQLite](https://github.com/xmikedanielsx/MSSQLToSQLite) (gusmando و همکاران، ۲۰۰۸ تا ۲۰۲۳).

دلیل ساخت این نسخه: در یک مهاجرت واقعی (SQL Server 2025 → SQLite → SQL Server 2022) مبدل اصلی و همه‌ی واردکننده‌های موجود
**نوع ستون‌ها را بی‌صدا خراب می‌کردند**؛ اپلیکیشن ASP.NET Core ما در اجرا با این خطا از کار افتاد:
`InvalidCastException: Unable to cast object of type 'System.Int64' to type 'System.Int32'`
چون `bit` به `nvarchar`، `datetime2` به `nvarchar`، `int` به `bigint` تبدیل شده و کلیدهای اصلی کلاً حذف شده بودند.

هر دو جهت بازنویسی شده‌اند تا رفت‌وبرگشت کامل، schema مبدأ را **دقیقاً** بازتولید کند — روی یک دیتابیس عملیاتی
(۳۲ جدول، ۳۲۶ ستون، حدود ۲۰ هزار رکورد) تست شد: **صفر اختلاف تایپ، صفر از دست دادن داده**.

---

## چه چیزهایی در این نسخه تغییر کرده

### هر دو جهت

| فیکس | قبل | بعد |
|---|---|---|
| **تشخیص کلید اصلی (PK)** | `sp_pkeys` — روی **SQL Server 2025 هیچ ردیفی برنمی‌گرداند** و همه‌ی PKها از خروجی حذف می‌شدند | `INFORMATION_SCHEMA.TABLE_CONSTRAINTS / KEY_COLUMN_USAGE` (مستقل از نسخه، پارامتری) |
| **حفظ نام تایپ‌ها** | `bigint→integer`، `tinyint→smallint`، `nchar→char`، `datetimeoffset→datetime` (مقدار offset نابود می‌شد!)، `decimal→numeric` (دقت و مقیاس گم می‌شد) | نام تایپ اصلی عیناً حفظ می‌شود (`bigint`، `datetime2`، `datetimeoffset`، `nvarchar(450)` و…) تا مسیر برگشت دقیقاً همان را بازسازی کند |
| **وفاداری عددی** | `numeric/decimal/money → DbType.Double` — مقادیر حین کپی گرد می‌شدند | `DbType.Decimal` + `numeric(p,s)` با دقت/مقیاس خوانده‌شده از مبدأ |
| **مپ‌های گم‌شده** | `datetimeoffset`، `money`، `smallmoney`، `smalldatetime`، `varbinary`، `binary` با خطای *"Illegal DB type"* کل تبدیل را می‌شکستند | همه درست مپ شدند |
| **GUID** | کد دیباگ باقی‌مانده و مپ ناسازگار | مپ تمیز `uniqueidentifier`/`nvarchar` با پشتیبانی از گزینه‌ی treat-as-string |
| **`sql_variant` و `xml`** | `sql_variant → DbType.Object` → کرش | به‌صورت متن کپی می‌شوند |
| **شکست‌های بی‌صدا** | CLI همه‌ی استثناها را بلعید می‌کرد (`exit 0` با فایل خالی!) | پیشرفت و خطاها در کنسول چاپ می‌شوند |

### SQLite → SQL Server (مسیر کاملاً بازنویسی‌شده)

- **بازسازی وفادارانه‌ی تایپ‌ها** از نوع اعلام‌شده‌ی SQLite (`bit → BIT`، `datetime → DATETIME2`، `nvarchar(450) → NVARCHAR(450)`، `time → TIME`، `datetimeoffset → DATETIMEOFFSET` و…) همراه با تبدیل مقدار بر اساس تایپ مقصد.
- **بازسازی IDENTITY**: کلید اصلی عددیِ تک‌ستونی به‌صورت `IDENTITY(1,1)` ساخته می‌شود (قرارداد EF/SQL Server)، داده با `IDENTITY_INSERT ON` کپی و seed با `DBCC CHECKIDENT` بازگردانده می‌شود.
- **انتشار عرض عددی روی گراف FK**: کلیدهای identity در SQLite به `integer` فرومی‌ریزند (محدودیت AUTOINCREMENT) در حالی که ستون‌های ارجاع‌دهنده `bigint`/`int` اصلی را حفظ می‌کنند؛ مبدل گراف کلیدهای خارجی را می‌پیماید و طرف مبهم را تا توافق دو سر هر FK عریض می‌کند — دیگر خطای *"Column … is not the same data type as referencing column …"* رخ نمی‌دهد.
- **بازیابی PK از طریق sidecar**: بعضی جدول‌ها PKشان در سمت SQLite گم شده؛ با کلید `"@primarykey:Table": "Col1,Col2"` برقرار می‌شود تا FKها حل شوند.
- **بازسازی ایندکس‌ها** (شامل unique)، **ساخت خودکار دیتابیس مقصد** و **گارد مقصدِ غیرخالی** که از import در دیتابیس دارای جدول جلوگیری می‌کند (جلوگیری از schemaهای مخلوط و خراب).
- **فایل sidecar اختیاری** `<file>.typemap.json` برای pin کردن تایپ دقیق هر ستون — مثلاً برای import فایل‌های ساخته‌شده با مبدل *قدیمی*، یا اجبار `"bigint identity"` روی یک ستون مشخص.

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

رابط گرافیکی WinForms (`ApplicationForm`) هم هر دو مسیر را به‌صورت تعاملی ارائه می‌دهد.

### فایل sidecar: `<فایل-sqlite>.typemap.json`

```json
{
  "Songs.Id": "bigint identity",
  "AspNetUsers.AccessFailedCount": "int",
  "@primarykey:Albums": "Id"
}
```

همه‌ی ورودی‌ها اختیاری‌اند؛ ستون‌های فاقد ورودی از نوع اعلام‌شده حل می‌شوند و برای ستون‌های `INTEGER` خام، عرض `INT`/`BIGINT`
به‌صورت داده‌محور (پروب MIN/MAX) تعیین می‌شود. **توصیه:** فایل SQLite را با *همین* نسخه بسازید — آن‌وقت اصلاً به sidecar نیازی
نیست و رفت‌وبرگشت خودبه‌خود دقیق است.

---

## Verification (راستی‌آزمایی)

تست رفت‌وبرگشت روی یک دیتابیس عملیاتی ASP.NET Core (۳۲ جدول / ۳۲۶ ستون / حدود ۲۰ هزار رکورد):

```
SQL Server (مبدأ)   ->  SQLite  ->  SQL Server (بازگشت)
ستون‌ها:      ۳۲۶    =    ۳۲۶     =       ۳۲۶
اختلاف تایپ:    ۰           -            ۰
رکوردها:    یکسان          -         یکسان
PKها:       ۳۱/۳۱          -         ۳۱/۳۱
```

شامل: `bit`، `datetime2`، `datetimeoffset`، `time`، `int`، `bigint identity`، `nvarchar(n)`، `nvarchar(max)`.

## پیش‌نیازها

- NET 10. (برای CLI کلاس‌کتابخانه و UI وین‌فرمز)
- پکیج‌های `System.Data.SQLite` و `System.Data.SqlClient` (NuGet، خودکار restore می‌شوند)
- پروژه‌های قدیمی net4.8 (`Converter` و `DbAccess`) فقط برای مرجع نگه داشته شده‌اند؛ از `ClassLibrary`/`ApplicationForm` نسخه‌ی net10 استفاده کنید.

## محدودیت‌های شناخته‌شده

- SQLite برای ستون‌های غیر `INTEGER` نمی‌تواند `AUTOINCREMENT` را بیان کند؛ بنابراین عرض دقیق ستون‌های identity فقط
  **اگر فایل با مبدل قدیمی ساخته شده باشد** در فایل SQLite از دست می‌رود. فایل‌های ساخته‌شده با همین نسخه تایپ اصلی را حمل می‌کنند
  و دقیق برمی‌گردند. برای فایل‌های قدیمی از sidecar استفاده کنید.
- مقادیر `decimal`/`money` از affinity نوع NUMERIC در SQLite (ذخیره‌سازی IEEE-754) عبور می‌کنند؛ تا حدود ۱۵ رقم معنادار حفظ
  می‌شود — برای مبالغ رایج کافی است، اما `decimal(38,x)` با دقت بالا را بعد از import چک کنید.
- مسیر SQLite → SQL Server ویوها و تریگرها را بازسازی نمی‌کند.

## سازگاری

- مبدأ تست‌شده: SQL Server 2025 (تبدیل به SQLite) و SQL Server 2022 (بازگشت از SQLite) — و رفت‌وبرگشت کامل بین این دو.
- مسیر برگشت روی هر نسخه‌ای که `INFORMATION_SCHEMA` و `IDENTITY` استاندارد را پشتیبانی کند کار می‌کند.

## قدردانی

- مبدل اصلی: gusmando — *SQL Server to SQLite Converter* (CodeProject، ۲۰۰۸)
- به‌روزرسانی NET 6. و فورک: [xmikedanielsx/MSSQLToSQLite](https://github.com/xmikedanielsx/MSSQLToSQLite) (۲۰۲۳)
- نسخه‌ی حفظ تایپ‌ها: بازنویسی دوطرفه، سازگاری SQL Server 2025، بازسازی IDENTITY/گراف FK، CLI (۲۰۲۶)

تحت همان شرایط مجوز پروژه‌ی اصلی منتشر می‌شود (CodeProject Open License / CPOL).
