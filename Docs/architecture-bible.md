# PgnTools V5 - Unified Architecture Bible (WPF Edition)
**Version:** 5.1.0-WPF
**Target:** .NET 8 LTS (`net8.0-windows`)

---

# PART 1: SYSTEM OVERVIEW
* **Performance:** Instant load of 1GB+ PGN files via Memory Mapped Files (MMF).
* **Framework:** WPF (Windows Presentation Foundation) for stability and maturity.
* **Structure:** Clean Architecture (Core, Infrastructure, Desktop) compiling to **One EXE**.
* **Stability:** Strict disposal patterns for MMF handles to prevent file-locking.

# PART 2: DATA SPECIFICATIONS

## 2.1 The `GameRecord` Struct (32 Bytes)
*Strictly Blittable. Layout must remain Sequential for MemoryMarshal.Cast operations.*

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)] 
public struct GameRecord
{
    public long FileOffset;       // 8 bytes
    public int Length;            // 4 bytes
    public int WhiteNameId;       // 4 bytes
    public int BlackNameId;       // 4 bytes
    public ushort WhiteElo;       // 2 bytes
    public ushort BlackElo;       // 2 bytes
    public byte Result;           // 1 byte
    public byte EcoCategory;      // 1 byte
    public byte EcoNumber;        // 1 byte
    public byte Flags;            // 1 byte
    public uint DateCompact;      // 4 bytes
}