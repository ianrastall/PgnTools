# Project_Specs.md

## 1. The PGN Binary Index (`.pbi`)

**Extension:** `.pbi` (Pgn Binary Index)
**Concept:** A custom binary format generated alongside every `.pgn` file. It consists of three sections: **Header**, **Game Records** (Fixed Width), and a **String Heap**.

### A. File Structure Layout

```text
[Header Section]      -> 16 Bytes
[Game Records Array]  -> (GameCount * 32 Bytes)
[String Heap]         -> Variable Length (Serialized Dictionary)

```

### B. Detailed Byte Layout

#### 1. Header (16 Bytes)

| Offset | Type | Description |
| --- | --- | --- |
| 0 | `char[8]` | Magic Signature: "PGNIDXv3" |
| 8 | `uint` | Version Number (Set to 1) |
| 12 | `uint` | **GameCount** (Number of games in index) |

#### 2. Game Record Struct (32 Bytes per Game)

*Designed for `MemoryMarshal.Cast<byte, GameRecord>` high-performance slicing.*

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GameRecord
{
    public long FileOffset;       // 8 bytes: Position in .pgn file
    public int Length;            // 4 bytes: Length of game text
    public int WhiteNameId;       // 4 bytes: Index into String Heap
    public int BlackNameId;       // 4 bytes: Index into String Heap
    public ushort WhiteElo;       // 2 bytes: 0 if unknown
    public ushort BlackElo;       // 2 bytes: 0 if unknown
    public byte Result;           // 1 byte: 0=*, 1=1-0, 2=0-1, 3=1/2
    public byte ECO_Category;     // 1 byte: ASCII char (e.g., 'A', 'B')
    public byte ECO_Number;       // 1 byte: 0-99
    public byte SubVariation;     // 1 byte: Padding/Flags
    public uint DateCompact;      // 4 bytes: YYYYMMDD packed integer
    // Total: 32 Bytes
}

```

#### 3. String Heap (Tail of File)

* **Format:** A serialized `Dictionary<int, string>`.
* **Why:** Normalization. "Magnus Carlsen" appears 10,000 times in a file. We store it **once** in the heap with ID `1`. The `GameRecord` just stores `1`.
* **Reading:** On load, read the Header, read the Struct Array (super fast), then deserialize the Heap into a `string[]`.

---

## 2. The Normalization Database (`master.db`)

**Technology:** SQLite (Microsoft.Data.Sqlite)
**Location:** `%AppData%\PgnTools\master.db`
**Purpose:** Stores user-defined "Truths" about names and sites. The `.pbi` indexer consults this DB when processing a new PGN.

### Schema Definitions

```sql
-- 1. Players Table: The canonical list of unique humans
CREATE TABLE Players (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CanonicalName TEXT NOT NULL UNIQUE, -- "Garry Kasparov"
    FideId INTEGER NULL
);

-- 2. NameMappings: Maps "dirty" names to Canonical IDs
CREATE TABLE NameMappings (
    RawName TEXT PRIMARY KEY,    -- "Kasparov, G."
    PlayerId INTEGER NOT NULL,   -- Links to Players(Id)
    FOREIGN KEY(PlayerId) REFERENCES Players(Id)
);

-- 3. Events Table: Canonical Tournament Names
CREATE TABLE Events (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CanonicalEvent TEXT NOT NULL UNIQUE
);

```

## 3. Workflow Logic

### The "Indexer" Service (Infrastructure Layer)

1. **Reader:** Streams the large `.pgn` file using `FileStream` (Buffered).
2. **Parser:** Uses `ReadOnlySpan<char>` to find tags (`[White "xxx"]`) quickly.
3. **Normalizer:**
* Looks up "xxx" in `master.db` (In-Memory Cache).
* If found -> Get ID.
* If not found -> Create new ID in internal dictionary.


4. **Writer:**
* Writes the `GameRecord` struct to a `MemoryStream`.


5. **Finalize:**
* Writes Header + Struct Buffer + String Heap to `.pbi` file.



---

### How to use this with the Gem

1. **Create this file** as `Project_Specs.md` and upload it to the chat.
2. **Prompt the Gem:**
> "I have uploaded the Project Specs. Based on the `GameRecord` struct in the specs and the `clean architecture` in the notebooks, please generate the `PgnTools.Core.Models.GameRecord` struct and the `PgnTools.Infrastructure.Services.BinaryPgnIndexer` class using `BinaryWriter`."



This explicitly separates the **"File Format"** (Speed) from the **"Database"** (Truth), which solves your concern about SQLite. You definitely want SQLite for the long-term memory of the app, but you want the Binary Index for the instant loading of the UI.