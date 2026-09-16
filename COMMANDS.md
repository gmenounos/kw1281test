# KW1281Test — Command Reference

A detailed reference for every command understood by **kw1281test**, a command-line
tool that talks the **KW1281** (and, for a few commands, **KW2000**) KWP "OnBoard
Diagnostics" protocol to modules in Volkswagen Group vehicles (VW, Audi, Škoda,
SEAT). It is used over a "dumb" serial→KKL or USB→KKL cable plugged into the
vehicle's OBD-II port.

This reference is derived directly from the current source code (`Program.cs`,
`Tester.cs`, `KW1281Dialog.cs`, `KwpCommon.cs`, the `Cluster\` and `EDC15\`
implementations), which is the authoritative source. Where the older `README.md`
or the project Wiki disagree, the code wins — a few commands are implemented but
not advertised in the on-screen `Usage:` help, and this document covers those too
(marked **hidden** below).

---

## Contents

- [How to run the tool](#how-to-run-the-tool)
- [Ports and cables](#ports-and-cables)
- [Baud rate](#baud-rate)
- [Controller address](#controller-address)
- [How argument numbers are parsed](#how-argument-numbers-are-parsed)
- [Command index](#command-index)
- [Commands](#commands)
  - [Discovery & identification](#1-discovery--identification)
  - [Fault codes](#2-fault-codes)
  - [Live readings & actuator tests](#3-live-readings--actuator-tests)
  - [Adaptation (learnings / codings)](#4-adaptation-learnings--codings)
  - [Coding](#5-coding)
  - [Radio safe codes](#6-radio-safe-codes)
  - [EEPROM / memory reads](#7-eeprom--memory-reads)
  - [EEPROM / memory writes](#8-eeprom--memory-writes)
  - [EDC15 ECU EEPROM](#9-edc15-ecu-eeprom)
  - [Cluster maintenance](#10-cluster-maintenance)
- [Safety notes](#safety-notes)
- [Where the output goes](#where-the-output-goes)

---

## How to run the tool

```
KW1281Test PORT BAUD ADDRESS COMMAND [args]
```

- **Windows:** `KW1281Test.exe PORT BAUD ADDRESS COMMAND [args]`
- **macOS / Linux:** `./kw1281test PORT BAUD ADDRESS COMMAND [args]`

If you run it with fewer than 4 arguments, it prints the `Usage:` help and exits.
Command names and all `START`/`LENGTH`/`ADDRESS`/`VALUE` style arguments are
**case-insensitive**, and address/size/value numbers accept decimal, `0x…`, or
`$…` (see [How argument numbers are parsed](#how-argument-numbers-are-parsed)).

### Example

```
# Identify a cluster (address 0x17) over COM4 at 10400 baud
KW1281Test.exe COM4 10400 17 ReadIdent

# Get the immobilizer SKC of a cluster
./kw1281test A12JCDR4 10400 17 GetSKC
```

---

## Ports and cables

The `PORT` argument selects which serial interface the tool opens. The tool
decides which driver backend to use based on the string you pass:

| Form of `PORT` | Backend used | Notes |
| --- | --- | --- |
| `COM1`, `COM4`, … | Generic / Windows serial | Any Windows serial port. |
| `/dev/ttyUSB0`, `/dev/ttyS0`, … | Linux serial | Must start with `/dev/` on Linux. |
| 8 uppercase alphanumerics, e.g. `A12JCDR4` | **FTDI (D2xx)** | An FTDI USB→serial device **serial number**. |

**Why the FTDI serial-number form exists:** the KW1281 wakeup is a *bit-banged
5 baud* (≈ 200 ms per bit) line that the tool generates by toggling the serial
port's break line at precise timing (`KwpCommon.BitBang5Baud`). The FTDI D2xx
driver gives the tool the low-level control (break line, exact baud changes) it
needs for that. On macOS and Linux you therefore **must** use an FTDI cable and
the D2xx drivers, and pass the cable's 8-character serial number as `PORT`.

The cable itself must be a **passive "dumb" KKL cable** (no protocol chip).
- Windows: cheap CH340-based cables generally work (install the CH340 driver);
  a legacy Ross-Tech cable can be used with its Virtual-COM-Port driver.
- macOS / Linux: FTDI-based cable with the D2xx drivers.

---

## Baud rate

`BAUD` is the post-wakeup data rate, in decimal. Typical values:

| Baud | Where you'll usually find it |
| --- | --- |
| `10400` | Most clusters, many ECUs. **Try this first.** |
| `9600` | Radios, CCM / central modules, airbag. |
| `4800` | Occasionally, on older modules. |

The wakeup message itself is always sent at 5 baud regardless of `BAUD`; `BAUD`
is the speed the controller and cable switch to after the handshake. If the log
shows "unexpected sync byte" style errors, the baud rate (or the cable/address)
is usually wrong — try `10400`, `9600`, then `4800`.

---

## Controller address

`ADDRESS` is the controller's K-line address. **Important: this argument is
parsed as a hexadecimal number** (`int.Parse(args[2], NumberStyles.HexNumber)`),
so you type the address in hex without any `0x`/`$` prefix:

```
KW1281Test.exe COM4 10400 17 ReadIdent     # 17 (hex) = 0x17 = cluster
KW1281Test.exe COM4  9600 1 ReadIdent      # 1  (hex) = 0x01 = engine ECU
```

Known controller addresses used in the code:

| Hex | Decimal | Module | Typical `BAUD` |
| --- | --- | --- | --- |
| `01` | 1 | Engine (ECU) | ? |
| `09` | 9 | Central Electric | ? |
| `15` | 21 | Airbag | `9600` |
| `17` | 23 | Instruments (cluster) | `10400` (often) / `9600` |
| `19` | 25 | CAN Gateway | ? |
| `25` | 37 | Immobilizer (immo box) | ? |
| `35` | 53 | Central Locking | ? |
| `37` | 55 | Navigation | ? |
| `46` | 70 | Comfort/Convenience Module (CCM) | `9600` |
| `56` | 86 | Radio (head unit) | `9600` |
| `7C` | 124 | Radio (manufacturing/delco test) | `9600` |

Commands check this address internally and many are only meaningful for one or a
few of these (noted per command below).

---

## How argument numbers are parsed

Any numeric argument that the tool parses with `Utils.ParseUint` (all `START`,
`LENGTH`, `ADDRESS`, `VALUE`, `CODING`, `WORKSHOP`, `LOGIN` values, and the
`WriteEdc15Eeprom` address/value pairs) accepts three forms:

- **decimal:** `2048`
- **`0x` hex:** `0x800`
- **`$` hex:** `$800`

The lone exception is the `ADDRESS` positional argument (controller address),
which is always plain hex, as described above.

---

## Command index

All commands the tool will actually execute (from the dispatcher in
`Program.Run`). Commands marked **hidden** are implemented but not listed in the
on-screen `Usage:` help.

| Command | Short description | Section |
| --- | --- | --- |
| `AutoScan` | Scan every K-line address for a live module | [1](#1-discovery--identification) |
| `ReadIdent` | Read the module's identification text (part no., etc.) | [1](#1-discovery--identification) |
| `GetClusterId` | **hidden** — read a cluster's anti-theft ID byte pair | [1](#1-discovery--identification) |
| `GetSKC` | Read the immobilizer Super Key Code (SKC) | [1](#1-discovery--identification) |
| `ReadSoftwareVersion` | **hidden** — read cluster (VDO) software version | [1](#1-discovery--identification) |
| `ReadFaultCodes` | Read stored fault/DTC codes | [2](#2-fault-codes) |
| `ClearFaultCodes` | Clear stored fault/DTC codes | [2](#2-fault-codes) |
| `ActuatorTest` | Run the module's actuator output tests (interactive) | [3](#3-live-readings--actuator-tests) |
| `GroupRead` | Read a measuring-values / diagnostic group (interactive) | [3](#3-live-readings--actuator-tests) |
| `BasicSetting` | Read a "basic setting" group (interactive) | [3](#3-live-readings--actuator-tests) |
| `AdaptationRead` | Read an adaptation (learning) channel | [4](#4-adaptation-learnings--codings) |
| `AdaptationTest` | Set an adaptation channel value **without** storing it | [4](#4-adaptation-learnings--codings) |
| `AdaptationSave` | Set **and store** an adaptation channel value | [4](#4-adaptation-learnings--codings) |
| `FindLogins` | **hidden** — brute-force which login codes are accepted | [4](#4-adaptation-learnings--codings) |
| `SetSoftwareCoding` | Write the module's software coding + workshop code | [5](#5-coding) |
| `ClarionVWPremium4SafeCode` | Read a Clarion VW Premium 4 radio's safe code | [6](#6-radio-safe-codes) |
| `DelcoVWPremium5SafeCode` | Read a Delco VW Premium 5 radio's safe code | [6](#6-radio-safe-codes) |
| `ReadEeprom` | Read one byte from EEPROM | [7](#7-eeprom--memory-reads) |
| `ReadROM` | Read one byte from ROM | [7](#7-eeprom--memory-reads) |
| `ReadRAM` | Read one byte from RAM | [7](#7-eeprom--memory-reads) |
| `DumpEeprom` | Dump an EEPROM range to a file | [7](#7-eeprom--memory-reads) |
| `DumpRom` | Dump a ROM range to a file | [7](#7-eeprom--memory-reads) |
| `DumpRam` | Dump a RAM range to a file | [7](#7-eeprom--memory-reads) |
| `DumpMarelliMem` | Dump a Magneti Marelli cluster EEPROM to a file | [7](#7-eeprom--memory-reads) |
| `DumpRBxMem` | Dump a Bosch RB4/RB8 cluster memory (KW2000) | [7](#7-eeprom--memory-reads) |
| `DumpRBxMemOdd` | **hidden** — like `DumpRBxMem`, odd-parity wakeup | [7](#7-eeprom--memory-reads) |
| `DumpCcmRom` | **hidden** — dump a CCM/central-locking ROM (64 KB) | [7](#7-eeprom--memory-reads) |
| `DumpClusterNecRom` | **hidden** — dump a VDO cluster NEC-ROM (64 KB) | [7](#7-eeprom--memory-reads) |
| `DumpMem` | Dump a VDO cluster's raw memory (custom command) | [7](#7-eeprom--memory-reads) |
| `MapEeprom` | **hidden** — probe every EEPROM byte into a map file | [7](#7-eeprom--memory-reads) |
| `WriteEeprom` | Write one byte to EEPROM | [8](#8-eeprom--memory-writes) |
| `WriteRAM` | Write one byte to cluster RAM | [8](#8-eeprom--memory-writes) |
| `LoadEeprom` | Load a binary file into EEPROM starting at `START` | [8](#8-eeprom--memory-writes) |
| `DumpEdc15Eeprom` | Dump + display an EDC15 ECU EEPROM | [9](#9-edc15-ecu-eeprom) |
| `WriteEdc15Eeprom` | Dump, then patch specific EDC15 EEPROM bytes | [9](#9-edc15-ecu-eeprom) |
| `Reset` | **hidden** — reset a VDO cluster (custom command) | [10](#10-cluster-maintenance) |
| `ToggleRB4Mode` | Toggle a Bosch RB4 cluster between New/Adapted mode | [10](#10-cluster-maintenance) |

---

## Commands

### 1. Discovery & identification

#### `AutoScan`

```
KW1281Test PORT BAUD ADDRESS AutoScan
```

The `ADDRESS` argument is **ignored**. For each of the 128 possible K-line
addresses, and for both odd and even parity, the tool sends a KW1281 wakeup and
records which modules answer. KWP1281 responders are listed under `KWP1281:` and
modules that reply with a different (KW2000) keyword under `KWP2000:`.

Use this to discover what's on the bus when you don't know the module addresses.
`BAUD` still matters for the post-wakeup handshake, so `10400` is a reasonable
default. It is interactive only in that it takes a while (it pokes all 256
address/parity combinations).

#### `ReadIdent`

```
KW1281Test PORT BAUD ADDRESS ReadIdent
```

Wakes the module up and reads its identification string — typically the
part number plus a short description, e.g.:

```
ECU: 1J5920926CX   KOMBI+WEGFAHRSP VDO V01
```

This is the first command to run on any unknown module: it tells you the part
number (so you can look the cluster up in the *Supported Clusters* wiki page) and
the manufacturer (VDO, Marelli, Bosch, Motometer, …), which decides which of the
other commands will work.

#### `GetClusterId` *(hidden)*

```
KW1281Test PORT BAUD ADDRESS GetClusterId
```

Sends a *Security Access mode 1* challenge to the cluster and decodes the
cluster's 2-byte unique ID (the value the radio uses to detect the cluster has
been swapped into a different vehicle). The response bytes are de-obfuscated
(subtract constants, XOR, bit-count, rotate). Prints `Cluster Id: $XX $XX`.

#### `GetSKC`

```
KW1281Test PORT BAUD 17 GetSKC      # cluster
KW1281Test PORT BAUD 25 GetSKC      # immobilizer box
KW1281Test PORT BAUD 1  GetSKC      # engine ECU
```

Reads and prints the **Super Key Code (SKC)** — the 5-digit immobilizer code
needed to adapt new keys. Only supported for clusters (`0x17`), immobilizer
boxes (`0x25`) and engine ECUs (`0x01`); any other address is rejected with a
message.

How it works depends on the module, detected from the `ReadIdent` text:

- **Marelli (M73):** dumps the EEPROM and computes the SKC from it.
- **Audi C5 (part prefixes `4B0`, `4Z7`, `8D0`, `8Z0`):** unlocks and dumps the
  EEPROM, reads the SKC at `0x7E2/0x7E4/0x7E6` and warns if the redundant copies
  disagree.
- **VDO:** parses the part number, then reads the SKC from the correct EEPROM
  location (BCD-encoded) for either non-CAN (`919`) or CAN (`920`) part variants.
- **Bosch RB4 / RB8:** switches the cluster to **New Mode**, dumps two bytes at
  the SKC offset (RB4 `0x10046`, RB8 `0x1040E`) and prints the SKC. (RB4 must be
  in New Mode — see `ToggleRB4Mode`.)
- **Motometer BOO:** dumps 16 bytes and reads the BCD SKC.
- **VWZ3Z0 immo boxes (`1H0`, `6H0`, `7M0`):** reads ROM at `0x190`; for the
  IMMO BOX 2 family it sends an *unlock ROM* + *secure immo access* command
  first.
- **EDC15 ECU (`0x01`):** performs an EDC15 EEPROM dump and prints the SKC,
  odometer, VIN and immo number (see the EDC15 section).

If the cluster type isn't recognized it prints `Unsupported cluster: <ident>`.

#### `ReadSoftwareVersion` *(hidden)*

```
KW1281Test PORT BAUD 17 ReadSoftwareVersion
```

Only supported for a **VDO cluster**. Sends a cluster *custom* command
(`$84` with variations `0x00–0x03`) to read and print the cluster software
version strings.

### 2. Fault codes

#### `ReadFaultCodes`

```
KW1281Test PORT BAUD ADDRESS ReadFaultCodes
```

Wakes the module and reads all stored fault/DTC codes, printing one per line
under a `Fault codes:` header. If the module replies with something other than a
fault-code block, nothing is printed for that module.

#### `ClearFaultCodes`

```
KW1281Test PORT BAUD ADDRESS ClearFaultCodes
```

Sends the *fault codes delete* command. On success it prints `Fault codes
cleared.` if there were no codes left, or lists any that remain. If the module
NAKs, it prints `Failed to clear fault codes.`

### 3. Live readings & actuator tests

All three of these are **interactive**: they keep the session alive and read
your keystrokes. The command name is a positional argument; `GROUP` is the only
extra argument.

#### `ActuatorTest`

```
KW1281Test PORT BAUD ADDRESS ActuatorTest
```

Runs the module's *actuator test* routine, which drives its outputs (blinkers,
horns, relay clicks, gauge sweeps, …) so you can confirm hardware works. It
sends the first actuator test and prints the actuator name; you then:

- press **N** to advance to the next actuator, or
- press **Q** to stop.

The test ends on its own when the module reports the `End` actuator.

#### `GroupRead`

```
KW1281Test PORT BAUD ADDRESS GroupRead GROUP
    GROUP = group number (0-255)
            (group 0 = raw controller data)
```

Reads a *measuring values / diagnostic* group and keeps refreshing it. While
running you can:

- press the **Up arrow** to increment the group number,
- press the **Down arrow** to decrement it,
- press **Q** to quit.

Group `0` uses the raw-data-read path and prints raw byte values (with a text
label when the module supplies one). For other groups the module may return
either a decoded text label or a numeric value; both are shown.

#### `BasicSetting`

```
KW1281Test PORT BAUD ADDRESS BasicSetting GROUP
    GROUP = group number (0-255)
            (group 0 = raw controller data)
```

Same interactive behavior as `GroupRead`, but it uses the *basic setting*
read path (`BasicSettingRead` / `BasicSettingRawDataRead`) instead of the
measuring-values path. This corresponds to the "Basic Settings" function in
VAG diagnostic software, used to check a module's basic self-test / operating
status. Group `0` again selects raw data.

### 4. Adaptation (learnings / codings)

Adaptation channels (a.k.a. "learnings" or "coding bytes") are per-module
calibration values. The three commands share the same argument shape; `LOGIN`
is optional and, when present, the tool performs a *login* (seed/key) before the
adaptation command — required by many modules to allow the write.

- `CHANNEL` — the adaptation channel number, `0`–`99`.
- `VALUE` — the value to set, `0`–`65535`.
- `LOGIN` — an optional login code, `0`–`65535`. If omitted, no login is sent
  (works only on modules/channels that don't require one). The workshop code
  used for the login is taken automatically from the module's identification.

#### `AdaptationRead`

```
KW1281Test PORT BAUD ADDRESS AdaptationRead CHANNEL [LOGIN]
```

Reads the current value of the given channel and prints `Adaptation value: N`.

#### `AdaptationTest`

```
KW1281Test PORT BAUD ADDRESS AdaptationTest CHANNEL VALUE [LOGIN]
```

Sends a *test* adaptation value. This applies the value **without committing it
to non-volatile storage** — useful for trying a setting live (e.g. adjusting a
calibration to see its effect) before you decide to save it.

#### `AdaptationSave`

```
KW1281Test PORT BAUD ADDRESS AdaptationSave CHANNEL VALUE [LOGIN]
```

Sends a *save* adaptation value: applies **and permanently stores** the value in
the module. This is the command used, for example, to adapt the number of keys
for the immobilizer (e.g. channel 21 on a cluster). The workshop code is
included in the save block.

> **Common example — adapting an Immo3 cluster to 2 keys (SKC `01111`):**
> ```
> KW1281Test.exe COM4 10400 17 AdaptationSave 21 2 01111
> ```

#### `FindLogins` *(hidden)*

```
KW1281Test PORT BAUD ADDRESS FindLogins LOGIN
    LOGIN = a known-good login code (0-65535)
```

Brute-forces the login space: with a *known-good* login as the baseline, it
iterates every candidate code from `0` to `65535`, attempting a second login
after each, and prints every code that succeeds. This is a research/forensics
helper for discovering which login codes a module accepts. It can take a very
long time and re-wakes the module whenever the session times out.

### 5. Coding

#### `SetSoftwareCoding`

```
KW1281Test PORT BAUD ADDRESS SetSoftwareCoding CODING WORKSHOP
    CODING   = software coding, 0-32767
    WORKSHOP = workshop code,   0-99999
```

Writes the module's **software coding** and **workshop code** (the "coding" /
"long coding" you see in VAG tools). Validation: `CODING` must be ≤ 32767 and
`WORKSHOP` must be ≤ 99999.

The coding is packed into the KW1281 *software coding* block; a workshop code
above 65535 overflows into the low bit of the coding (this is by design to match
how the protocol stores the combined value). After sending, the tool reads the
module's coding back and reports `Software coding set.` only if both the coding
and workshop code match what you sent, otherwise `Failed to set software coding.`

> Always read the current coding first (it's part of the module identification
> shown on wakeup, and in the `CodingWscBlock`). If you need to change only the
> workshop code, keep the original coding value.

### 6. Radio safe codes

These read the anti-theft "safe code" from a head unit so it can be paired with
the cluster. Each is only valid for one radio address.

#### `ClarionVWPremium4SafeCode`

```
KW1281Test PORT BAUD 56 ClarionVWPremium4SafeCode
```

Only supported for **radio address `0x56`**. Sends the `ReadSafeCode` command
(block title `$F0`, read) and prints the 16-bit safe code as 4 hex digits
(`Safe code: XXXX`).

#### `DelcoVWPremium5SafeCode`

```
KW1281Test PORT BAUD 7C DelcoVWPremium5SafeCode
```

Only supported for the **radio manufacturing address `0x7C`**. Performs a login
using the "DELCO" secret, then reads two bytes of ROM/EEPROM at `0x0014` and
prints them as the safe code.

### 7. EEPROM / memory reads

The KW1281 protocol distinguishes several memory spaces:

- **EEPROM** — non-volatile memory holding codings, the SKC, odometer, etc.
- **ROM** — (mask/flash) program/read-only memory.
- **RAM** — volatile working memory.

Most read commands **unlock the module first** (login/seed-key) because memory
access is permission-gated. For a VDO cluster that means the cluster-specific
seed/key dance; for CCM / central-locking / central-electric it sends a fixed
login (the codes VDS-PRO uses). If a read is NAK'd, the tool prints a
`… read failed` message.

Single-byte reads print `Address N ($NNNN): Value V ($XX)`.

#### `ReadEeprom`

```
KW1281Test PORT BAUD ADDRESS ReadEeprom ADDRESS
```

Reads a single byte from EEPROM at `ADDRESS` (16-bit address space).

#### `ReadROM`

```
KW1281Test PORT BAUD ADDRESS ReadROM ADDRESS
```

Reads a single byte from ROM at `ADDRESS`.

#### `ReadRAM`

```
KW1281Test PORT BAUD ADDRESS ReadRAM ADDRESS
```

Reads a single byte from RAM at `ADDRESS`.

#### `DumpEeprom`

```
KW1281Test PORT BAUD ADDRESS DumpEeprom START LENGTH [FILENAME]
```

Dumps `LENGTH` bytes of EEPROM starting at `START` to `FILENAME` (default name
derived from the module identification + start address, e.g.
`…_0x0000_eeprom.bin`). Only supported for **cluster**, **CCM**, **central
locking** and **central electric** addresses; other addresses print a "not
supported" message. A cluster dump uses the VDO seed/key path and reads in 16-byte
blocks; CCM-family dumps read in 8-byte blocks. If any block can't be read it is
zero-filled and a warning is printed.

#### `DumpRom`

```
KW1281Test PORT BAUD ADDRESS DumpRom START LENGTH [FILENAME]
```

Dumps `LENGTH` bytes of ROM from `START` to `FILENAME` (default
`rom_0xSTART.bin`). Reads in 8-byte blocks after unlocking the module. Used, for
example, to pull the full program from a module.

#### `DumpRam`

```
KW1281Test PORT BAUD ADDRESS DumpRam START LENGTH [FILENAME]
```

Dumps `LENGTH` bytes of RAM from `START` to `FILENAME` (default
`ram_0xSTART.bin`). Reads in 8-byte blocks after unlocking.

#### `DumpMarelliMem`

```
KW1281Test PORT BAUD 17 DumpMarelliMem START LENGTH [FILENAME]
```

Dumps a **Magneti Marelli** cluster's EEPROM (cluster address `0x17` only).
Marelli clusters read memory by uploading a small program to the cluster and
running it, so this uses the Marelli cluster handler. Because different Marelli
variants have different EEPROM sizes/starts, if you're unsure which region is
valid the wiki suggests probing, e.g.:

```
kw1281test.exe COM1 9600 17 DumpMarelliMem 3072 1024
kw1281test.exe COM1 9600 17 DumpMarelliMem 14336 2048
```

#### `DumpRBxMem`

```
KW1281Test PORT BAUD 17 DumpRBxMem START LENGTH [FILENAME]
```

Dumps memory of a **Bosch RB4 / RB8** cluster (cluster `0x17`). These clusters
communicate memory access over **KW2000** (not KW1281), so the tool wakes the
module in KW2000 mode (even parity), unlocks it, and dumps `LENGTH` bytes from
`START`. Default file name `RBx_0xSTART_mem.bin`.

#### `DumpRBxMemOdd` *(hidden)*

```
KW1281Test PORT BAUD 17 DumpRBxMemOdd START LENGTH [FILENAME]
```

Exactly like `DumpRBxMem`, but the KW2000 wakeup uses **odd parity**
(`evenParityWakeup: false`) instead of even. For clusters that only answer the
KW2000 keyword with odd parity.

#### `DumpCcmRom` *(hidden)*

```
KW1281Test PORT BAUD 46 DumpCcmRom
```

Dumps the full **64 KB CCM ROM** (CCM `0x46` or central-locking `0x35` only).
It reads the CCM ROM in a `segment / msb / lsb` layout (16 × 16 × 32, in 8-byte
blocks) and writes it to a **fixed** file `ccm_rom_dump.bin` (the `FILENAME`
argument is not parsed for this command). Unreadable bytes are zero-filled and a
warning is printed.

#### `DumpClusterNecRom` *(hidden)*

```
KW1281Test PORT BAUD 17 DumpClusterNecRom
```

Dumps the low **64 KB of a VDO cluster's NEC controller ROM** (cluster `0x17`
only) via a custom cluster command, in 16-byte blocks, to a fixed file
`cluster_nec_rom_dump.bin`. (For MFA clusters this is the whole ROM; FIS
clusters have a 128 KB ROM and only the low 64 KB is retrieved.)

#### `DumpMem`

```
KW1281Test PORT BAUD 17 DumpMem START LENGTH [FILENAME]
```

Dumps a VDO cluster's raw memory via its *custom "read memory"* command (3-byte
addresses, so it can reach beyond 16 bits). Cluster `0x17` only; performs the
VDO seed/key unlock first. Default file `cluster_mem_0xSTART.bin`.

#### `MapEeprom` *(hidden)*

```
KW1281Test PORT BAUD ADDRESS MapEeprom
```

Probes **every** EEPROM address `0x0000–0xFFFF` once and writes a 64 KB map file
where each byte is `0xFF` if that address reads (is live) or `0x00` if it
NAK'd/is absent. Useful for discovering the real size/boundaries of a module's
EEPROM. Cluster uses the VDO map path; CCM-family uses the byte-probe path.
Output file is fixed (`eeprom_map.bin` or `ccm_eeprom_map.bin`; the `FILENAME`
argument is not parsed for this command).

### 8. EEPROM / memory writes

> ⚠️ **These modify the module.** A wrong value can disable a feature, break the
> immobilizer pairing, or make a module inoperable. Dump the affected range first
> (`DumpEeprom`) so you have a known-good backup, and change only what you mean to.

#### `WriteEeprom`

```
KW1281Test PORT BAUD ADDRESS WriteEeprom ADDRESS VALUE
    ADDRESS = EEPROM address
    VALUE   = 0-255
```

Writes a single byte to EEPROM after unlocking the module. The tool verifies the
module's write-acknowledge block echoes the address/value; a mismatch or NAK is
reported as a failed write.

> **Example — VW T5 comfort module (CCM `0x46`), disable three window-roll-up
> values (from `10` to `2`):**
> ```
> kw1281test /dev/ttyUSB0 9600 46 WriteEeprom 4361 2
> kw1281test /dev/ttyUSB0 9600 46 WriteEeprom 4362 2
> kw1281test /dev/ttyUSB0 9600 46 WriteEeprom 4363 2
> ```

#### `WriteRAM`

```
KW1281Test PORT BAUD 17 WriteRAM ADDRESS VALUE
    VALUE = 0-255
```

Writes a single byte to cluster RAM (cluster `0x17` only), performing the VDO
seed/key unlock first. RAM is volatile — this only affects the module until its
next power cycle.

#### `LoadEeprom`

```
KW1281Test PORT BAUD ADDRESS LoadEeprom START FILENAME
    START    = EEPROM start address
    FILENAME = file containing the binary data to write
```

Loads a binary file into EEPROM starting at `START`. Only supported for cluster,
CCM, central locking and central electric. It unlocks the module, reads
`FILENAME` from disk, and writes it in 16-byte blocks (cluster) or 8-byte blocks
(CCM-family), verifying each block. If `FILENAME` does not exist it aborts. This
is how you restore a previously dumped EEPROM.

### 9. EDC15 ECU EEPROM

These two commands work on **Bosch EDC15 engine ECUs** (engine address `0x01`).
They are the only commands that fully use the **KW2000** diagnostic protocol.
Both first end the KW1281 session and re-wake the ECU in KW2000 mode, then run a
built-in **loader program** (uploaded to ECU RAM at `0x40E000` and executed)
that drives the ECU's serial EEPROM. Authentication is level `0x41` (a real seed
is only computed when the ECU is already awake in KW2000 mode).

Because they replace code running in the ECU, **a failed write can brick the
ECU** — the wiki recommends disconnecting the battery for a minute to recover. A
backup of the whole EEPROM is written *before* any bytes are changed. This is the
**EEPROM, not the flash** — you cannot upload a tune this way.

#### `DumpEdc15Eeprom`

```
KW1281Test PORT BAUD 1 DumpEdc15Eeprom [FILENAME]
```

Dumps the full **512-byte EDC15 EEPROM** to `FILENAME` (default
`EDC15_EEPROM.bin`) and then prints decoded information:

- **SKC** (5-digit immobilizer code)
- **Odometer** (km)
- **VIN**
- **Immo number / ID**
- **Immo status** — `Off` if EEPROM bytes `0x1B0` and `0x1DE` are both `0x60`,
  otherwise `On`.

#### `WriteEdc15Eeprom`

```
KW1281Test PORT BAUD 1 WriteEdc15Eeprom ADDRESS1 VALUE1 [ADDRESS2 VALUE2 ...]
    ADDRESS = 0-511
    VALUE   = 0-255
```

Dumps the EEPROM to a timestamped backup file
(`EDC15_EEPROM_<timestamp>.bin`), then writes each `ADDRESS VALUE` pair and
reboots the ECU. Addresses and values must be even-count pairs; addresses are
limited to `0–511` and values to `0–255`. Addresses above `0xFF` are written via
the EEPROM "page 1" path, the rest via "page 0".

The well-known use case is **EDC15 immobilizer-off** (set both immo bytes to
`0x60`):

```
kw1281test.exe com1 9600 1 WriteEdc15Eeprom 0x1B0 0x60 0x1DE 0x60
```

### 10. Cluster maintenance

#### `Reset` *(hidden)*

```
KW1281Test PORT BAUD 17 Reset
```

Sends a VDO cluster *custom reset* command (`$82`). Cluster `0x17` only. Used,
for instance, after a mode change to make the cluster re-initialize.

#### `ToggleRB4Mode`

```
KW1281Test PORT BAUD 17 ToggleRB4Mode
```

Wakes a **Bosch RB4** cluster in KW2000 mode (even parity), unlocks it, and
toggles between its two operating modes — **New Mode (4)** and **Adapted Mode
(6)** — then resets the cluster. The cluster **must be in New Mode to read the
SKC** (via `GetSKC`) but must be in **Adapted Mode for normal use**, so the
typical flow is: `GetSKC` → if it reports "not in New mode", `ToggleRB4Mode` →
`GetSKC` → `ToggleRB4Mode` (back to Adapted).

---

## Safety notes

- **Wiring / power:** ignition on (or the module's power supply active) and a
  working, correctly-drilled KKL cable are prerequisites for every command.
- **Wakeup retries:** if the module doesn't wake, the tool retries 3 times and,
  on failure, reminds you to check the cable, drivers, ignition, address and
  baud rate.
- **Writes are destructive:** `WriteEeprom`, `WriteRAM`, `LoadEeprom`,
  `SetSoftwareCoding`, `AdaptationSave` and `WriteEdc15Eeprom` change module
  state. Always back up first (dump the range, or note the current coding).
- **EDC15 is highest-risk:** it runs a loader in the running ECU; an interruption
  can brick it. Disconnect the battery for a minute to recover.
- **SKC/adaptation:** writing an SKC or key-count adaptation with the wrong value
  can lock you out of the immobilizer.

## Where the output goes

Everything the tool logs goes to **both** the console and a **`KW1281Test.log`**
file created in the current working directory (appended on each run). This log
contains the raw wakeup handshake, block titles, seed/key exchange, and every
byte sent/received, which is exactly what the project maintainers ask for when
you open a support issue. Interactive prompts (actuator-test keys, group-read
arrows) are printed to the console only.

---

## Quick reference — supported addresses per command

| Command | Addresses it accepts |
| --- | --- |
| `ReadIdent`, `ReadFaultCodes`, `ClearFaultCodes`, `SetSoftwareCoding`, `ActuatorTest`, `GroupRead`, `BasicSetting`, `AdaptationRead/Test/Save`, `FindLogins`, `MapEeprom` | most modules (some features depend on the module) |
| `DumpEeprom`, `LoadEeprom`, `DumpMem`, `WriteRAM`, `DumpMarelliMem`, `DumpRBxMem/Odd`, `DumpClusterNecRom`, `ReadSoftwareVersion`, `Reset`, `ToggleRB4Mode`, `GetClusterId` | cluster `0x17` |
| `DumpEeprom`, `LoadEeprom` (also CCM-family) | cluster `0x17`, CCM `0x46`, central locking `0x35`, central electric `0x09` |
| `DumpCcmRom` | CCM `0x46`, central locking `0x35` |
| `GetSKC` | cluster `0x17`, immo `0x25`, ECU `0x01` |
| `ReadEeprom`, `ReadROM`, `ReadRAM`, `DumpRom`, `DumpRam`, `WriteEeprom` | cluster / CCM / central modules (unlock-dependent) |
| `ClarionVWPremium4SafeCode` | radio `0x56` |
| `DelcoVWPremium5SafeCode` | radio manufacturing `0x7C` |
| `DumpEdc15Eeprom`, `WriteEdc15Eeprom` | engine ECU `0x01` |

