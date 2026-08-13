# Vendored legacy source

Verbatim copies of the legacy AttendanceSystem artifacts supplied for the migration. Vendored so every `L-xx` / `D-xx` citation in [../architecture/legacy-analysis.md](../architecture/legacy-analysis.md) stays resolvable.

**Do not edit.** These are evidence, not source.

| File | Lines | SHA-256 |
|---|---|---|
| `Database/schema.sql` | 58 | `8cffcda6522ab46981d1cf658c3128976ca6f82ec97fe1d16a30afef5c7c48e9` |
| `Database/sp_GetStudentAttendance.sql` | 46 | `36232b115faee328dedf713fccbcf02aab5dced07ae18459c4cb7dc095a74b02` |
| `Database/sp_SaveDailyAttendance.sql` | 122 | `f18c96dd65a3048a5b79f034eb59385ad92034dee339c930bbd6b7933612c227` |
| `Forms/frmDailyAttendance.frm` | 139 | `227ff222f110a6dd9c5ed19fc31bd01f27f312a8261f9ce483fd39d03c2bb8c8` |

365 lines total. Original location: `~/Downloads/AttendanceSystem`.

This is the complete set that was supplied. Nine further objects are referenced by these files but were never provided — six database objects plus a VB helper, a Crystal report and a config file. See §1 of the analysis.
