# Contacts data generator

Console tool that writes large contact files for performance runs of the importer: browser parsing and upload, and server throughput. It supports every import format (CSV, TSV, NDJSON, JSON array, XLSX), plain or compressed (`.gz`, single-entry `.zip`).

- Same options and `--seed` → byte-identical file. Data does not depend on the format: a CSV and an XLSX made with the same seed hold the same rows.
- Streaming writes: memory stays flat for any row count (about 45 MB; XLSX with shared strings about 90 MB).
- Generated files contain fake data only (`example.com` / `.org` / `.net` addresses).

## Run

From the repository root (Release is much faster than Debug for large files):

```powershell
dotnet run -c Release --project tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator -- --rows 1000000 --format csv --out C:\temp\contacts-1m.csv
```

`--help` lists all options.

| Option | Default | Description |
| --- | --- | --- |
| `--rows <n>` | `10000` | Data rows (header not counted). `_` allowed: `1_000_000`. |
| `--format <f>` | `csv` | `csv`, `tsv`, `ndjson`, `json` (top-level array), `xlsx` |
| `--compress <c>` | `none` | `none`, `gz`, `zip` (one entry named like the output without `.zip`). `gz` is not allowed for `xlsx`. |
| `--out <path>` | `contacts-<rows>.<ext>[.gz\|.zip]` | Output file. Folders are created. |
| `--seed <int>` | `1` | Random seed. |
| `--duplicates <pct>` | `0` | Rows that repeat the email of an earlier row (other values differ). |
| `--invalid-emails <pct>` | `0` | Rows with an invalid email (`a.b.5(at)example.com`, `a.b.5@`, `a b 5`, `a.b.5@@example.com`). |
| `--empty-values <pct>` | `0` | Non-email cells left empty. |
| `--custom-columns <n>` | `0` | Extra text columns `Custom1`..`CustomN`. |
| `--consent-columns` | off | Adds `ConsentAgreed` (yes/no) and `ConsentDate` (set when agreed). |
| `--date-format <f>` | `iso` | Text dates: `iso` (`1985-04-23`, `2023-06-01T22:54:27`), `dmy` (`23.04.1985`), `mdy` (`04/23/1985`). XLSX always uses date cells. |
| `--key-offset <n>` | `0` | Number of the first email. Two runs share emails where their ranges overlap. |
| `--xlsx-shared-strings <true\|false>` | `true` | Shared strings table (like Excel) or inline strings. Test the reader with both. |
| `--quiet` | off | No progress lines. |

Exit codes: `0` file written, `1` write failed, `2` invalid options.

## Columns and data

`Email, FirstName, LastName, Company, JobTitle, City, Phone, Birthday`, then `Custom1..N`, then `ConsentAgreed, ConsentDate`. The names match the importer's automatic mapping suggestions for contact fields; consent and custom columns are mapped by hand.

- Email: `<first>.<last>.<number>@<domain>`, number = row key + `--key-offset`. Names come from the number only, so overlapping runs agree on name + email.
- Some names, companies and cities have non-ASCII characters; companies include `Smith, Jones & Co` and `The "Best" Coffee` (CSV quoting, XML escaping).
- Empty values: CSV/TSV empty field, JSON `null`, XLSX cell left out (sparse row).
- `ConsentAgreed`: CSV `yes` / `no`, JSON `true` / `false`, XLSX boolean cell.

### File details

- CSV / TSV: UTF-8 without BOM, CRLF, RFC 4180 quoting.
- NDJSON: one object per line (LF). JSON: `[`, one object per line separated by `,`, `]`. Non-ASCII text is written as UTF-8, not `\u` escapes.
- XLSX: one sheet `Contacts`, header in row 1, 1900 date system. `Birthday` = serial number with built-in date format 14; `ConsentDate` = serial number with fraction and custom format 164 `yyyy-mm-dd hh:mm:ss`. Maximum 1,048,575 data rows (Excel limit 1,048,576 rows incl. header). Shared strings: repeated strings are deduplicated until 200,000 unique strings, later new strings (mostly emails) get their own entry.
- `.zip` / `.xlsx`: written with `System.IO.Compression.ZipArchive`. An XLSX inside `--compress zip` is written to a non-seekable stream, so its entries use data descriptors (sizes after the data, general purpose flag bit 3). Readers must take sizes from the central directory.

## Perf run examples

```powershell
$gen = 'tests/Kentico.Xperience.Labs.ContactsImporter.Admin.DataGenerator'
$out = 'C:\temp\contacts-perf'

# 1M rows, each format
dotnet run -c Release --project $gen -- --rows 1000000 --format csv --out $out\contacts-1m.csv
dotnet run -c Release --project $gen -- --rows 1000000 --format tsv --out $out\contacts-1m.tsv
dotnet run -c Release --project $gen -- --rows 1000000 --format ndjson --out $out\contacts-1m.ndjson
dotnet run -c Release --project $gen -- --rows 1000000 --format json --out $out\contacts-1m.json
dotnet run -c Release --project $gen -- --rows 1000000 --format xlsx --out $out\contacts-1m.xlsx
dotnet run -c Release --project $gen -- --rows 1000000 --format xlsx --xlsx-shared-strings false --out $out\contacts-1m-inline.xlsx

# 10M rows (not for xlsx), compressed
dotnet run -c Release --project $gen -- --rows 10000000 --format csv --out $out\contacts-10m.csv
dotnet run -c Release --project $gen -- --rows 10000000 --format ndjson --compress gz --out $out\contacts-10m.ndjson.gz

# Realistic dirty data: duplicates, invalid emails, empty values, consent + custom columns
dotnet run -c Release --project $gen -- --rows 1000000 --duplicates 5 --invalid-emails 1 --empty-values 10 --consent-columns --custom-columns 5 --out $out\dirty-1m.csv

# Update run: second file overlaps the first by 500,000 emails
dotnet run -c Release --project $gen -- --rows 1000000 --out $out\first.csv
dotnet run -c Release --project $gen -- --rows 1000000 --key-offset 500000 --seed 2 --out $out\second.csv
```

## Measured output sizes (1M rows)

Default options (8 columns, no duplicates / empty values), Release build, Windows 11 dev laptop, local SSD. Peak = peak working set of the process.

| File | Size | Time | Peak |
| --- | ---: | ---: | ---: |
| `contacts-1m.csv` | 102.0 MB | 0.9 s | 45 MB |
| `contacts-1m.tsv` | 101.9 MB | 0.9 s | 45 MB |
| `contacts-1m.ndjson` | 192.4 MB | 5.5 s | 44 MB |
| `contacts-1m.json` | 193.3 MB | 4.0 s | 44 MB |
| `contacts-1m.xlsx` (shared strings) | 45.5 MB | 6.6 s | 90 MB |
| `contacts-1m-inline.xlsx` | 48.9 MB | 3.8 s | 49 MB |
| `contacts-1m.csv.gz` | 22.3 MB | 1.7 s | 48 MB |
| `contacts-1m.csv.zip` | 22.3 MB | 1.7 s | 49 MB |
| `contacts-1m.json.gz` | 38.3 MB | 8.6 s | 45 MB |
| `contacts-1m.xlsx.zip` | 44.1 MB | 6.7 s | 90 MB |

10M rows: CSV 1,029.5 MB in 5.9 s; NDJSON `.gz` 383.2 MB in 96 s. XLSX at the row limit (1,048,575 rows, consent columns): 61.6 MB in 7.8 s.

## Tests

The project is also the generator's NUnit test project (`dotnet test`; CI runs it with the solution): determinism per seed, every format and compression read back and compared to the CSV of the same seed, XLSX package parts / shared + inline strings / date styles, duplicate / invalid / empty percentages, `--key-offset` overlap, single-entry zip, option validation.
