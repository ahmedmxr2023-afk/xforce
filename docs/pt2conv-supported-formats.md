# pt2conv — supported input formats

`pt2conv` converts a **TriStation 1131 5.8 PLCopen XML export** into a `.pt2x`
project container that TriStation opens and builds without errors. The builder
never calls TriStation: the output is produced entirely by `pt2conv`.

Run the pre-flight check before converting:

```powershell
dotnet pt2conv.dll check  project.xml
dotnet pt2conv.dll buildxml project.xml project.pt2x
```

`buildxml` runs the same check and refuses to convert when a blocker is found.

## Conversion matrix

| Input | Status | Notes |
|---|---|---|
| TriStation 1131 XML (`fileHeader/@productName = "TriStation 1131"`) | **supported** | The only supported producer. |
| FBD function blocks and programs | **supported** | Diagrams, variables, wires, comments, dividers, sheets, drawing frames. |
| ST function blocks and programs | **supported** | The `.ST` body is copied byte-for-byte (CDATA content, CRLF). |
| Global tag table (`<ts:TAGNAME>` blocks) | **supported** | Rebuilt into `Tagnames.csv` (types, addresses, point metadata, groups, descriptions). |
| Hardware / chassis layout (`<ts:HARDWARE_ALLOCATION>`) | **supported** | Chassis, slots, module ids, point segments and offsets are rebuilt. |
| Execution list / tasks | **supported** | The first execution list is carried over. |
| Project-defined function blocks instantiated by programs | **supported** | Catalogue entries and snapshots are generated so instances resolve. |
| Standard library blocks (STDLIB/TCXLIB/TX1LIB) | **supported** | Identity and pin tables come from the installed 5.8 library captured in `Resources/`. |
| Ladder Diagram (LD) | **blocked** | No LD → FBD translation. |
| Sequential Function Chart (SFC) | **blocked** | No SFC conversion. |
| Instruction List (IL) and other body languages | **blocked** | Only FBD and ST bodies are converted. |
| User-defined data types (`<types><dataTypes><dataType>`) | **blocked** | UDTs are not created in the output project. |
| `<derived>` type that is neither a POU in the file nor a library element | **blocked** | Usually a UDT or a missing project POU. |
| Foreign exports (SimaticML, CODESYS, …) | **blocked** | `productName`/namespace is checked. SimaticML can be translated first with `pt2conv fromsimatic`. |
| Unknown block type (not in the library, not defined in the file) | **blocked** | Would open as an error block in TriStation. |
| TriStation 4.x export that stores each element twice (`<pou>` duplicated) | supported | The duplicate definition is dropped and a single diagram is written. Merging both copies, which is what 5.8 does on import, overlays the sheets and fails with `WF0032`. |
| Unconnected variable placement in a program drawing | supported | Dropped from the diagram (the declaration is kept); TriStation reports `WG0001` for a placement with no wiring. |
| Program-scope variables in a program's `<globalVars>` that are not in the tag table | supported | Written as program-local `VAR`; declaring them as globals makes 5.8 report `WF0078` (unresolved tagname). |
| Constant → block-input connection whose exported path points at stale coordinates | supported | Written as a direct pin link, the way 5.8's importer stores it; emitting the stale stub causes `WF0032`. |
| POU with no body | warning | Kept as a declaration only. |
| More than one configuration / task | warning | Only the first execution list is carried over. |
| Cause & Effect (CEM) data | warning | Not converted. |
| No tag declarations | warning | The tag database is written with TriStation's import defaults for typed columns. |

### Tag columns with no XML source

A TriStation export does not carry every `Tagnames.csv` column. For columns such as
`Retentive`, `IOModuleConfig`, `IOModuleTrigger`, `AI_Unit`, `PI_PointType`,
`PI_PointGearTeeth` and `PI_PointGearRatio`, `pt2conv` first keeps the scaffold project's
value for a tag with the same name and otherwise writes the value TriStation's importer
assigns (`False`, `0`, `0`, `mA`, `00`, `01`, `1.000000`). Leaving those cells empty makes
TriStation refuse to open the container (`WB0045: Error detected while reading file`), even
though every element, diagram and CRC is valid.

## Validation method

TriStation's `XMLImportExport.exe` is used **only as a test oracle**:

1. `pt2conv check` — pre-flight capability report.
2. `pt2conv buildxml` — our conversion.
3. TriStation imports the same XML into a fresh project and builds it.
4. TriStation builds **our** output with `ReBuildAll=True`.
5. Error codes from both logs are compared (`tools/Convert-Matrix.ps1`).

The corpus is the 22 TriStation projects in the customer export set
(`PACK`, `R33-42F5UpdSoe`, `R32-32`, `R33_HW_DBUpdSoe`, `SPARE_Replicated` ×4,
`TEMPLATES_*`, `33-71-73*`, …), covering FBD and ST, tags from 15 to 2,496,
and hardware from one chassis to replicated multi-chassis configurations.

## Command contract

```powershell
dotnet pt2conv.dll check  project.xml            # human report (exit 0 convertible / 3 blocked)
dotnet pt2conv.dll check  project.xml --json     # machine-readable report (canConvert, counts, blockers…)
dotnet pt2conv.dll buildxml project.xml out.pt2x
dotnet pt2conv.dll lint out.pt2x                # snapshot/catalogue/library identity guard # refuses blocked input (--force overrides)
dotnet pt2conv.dll buildxml project.xml out.pt2x
dotnet pt2conv.dll lint out.pt2x                # snapshot/catalogue/library identity guard --assume-multi-write
```

`--assume-multi-write` enables `EnableMultipleWrite` for tags that the XML shows
written at more than one site (the compiler otherwise stops with `PC0005`).
`check` already lists those tags, so the repair is explicit and auditable.

The converter also probes the installed TriStation version. It never calls
TriStation, but it reports when the host does not match the captured 5.8
library identity data (e.g. a newer TriStation is installed).

## Regression harness

`tools/Convert-Matrix.ps1` imports and builds the same XML with TriStation
(oracle) and with `pt2conv`, then compares error codes. It writes `matrix.csv`
and a machine-readable `golden.json` (`project`, `preflight`, `ours`, `oracle`)
that is suitable as a CI golden record. `.github/workflows/pt2conv.yml` builds
the converter and runs an offline container round-trip plus the `check --json`
contract on every push.

## Current corpus result

Regression harness `tools/Convert-Matrix.ps1` over the 22 customer exports
(both `System :` load-time and `Error detected :` compiler diagnostics are
counted):

- **21 / 22 convert and build with 0 errors** in TriStation out of the box.
- `PACK_Replicated` (`…E691E6`) reports `PC0005` (a tag written at two sites
  while `EnableMultipleWrite="No"`). Its own shipped `.pt2x` and TriStation’s
  re-import fail identically. `check` lists the tag, and
  `buildxml … --assume-multi-write` produces a build with **0 errors** (opt-in,
  because it relaxes a safety setting the export lost).
- `33-71-73_FB_Replicated` is blocked by pre-flight: 209 globals referenced by
  programs have no `TAGNAME` declaration and TriStation’s own import fails with
  `WF0078`.

The `SPARE_Replicated` family (previously `WC0140: Feature not implemented`)
was fixed by matching TriStation's declaration model exactly:

- `varVTType` comes from the interface section (input 0, output 1, local/inOut 3,
  global 5), not from how the wire happens to use the variable.
- Instance flag `0x04000000` (100663298) is set for **library** instances only in
  function blocks that are actually referenced by another element; all other
  instances use 33554434.
- Program globals never carry the 65536 flag.

Verified by comparing the declarations of all nine `33-71-73` function blocks
against a TriStation import: 1,160 / 1,160 declarations identical
(name, type, init value, description, varVTType, refCount, fbRefCount, flags).

## Allocation rules learned from the reference import

These were derived by importing the same XML with TriStation and diffing its
project against ours. They are now implemented in `TagGen`/`HwConfig`/`FbdGenerator`.

### Aliased tags

An aliased tag keeps its point coordinate in `Address` and carries the memory
word it resolves to in `PhysicalAddress`:

| Alias | Data type | Address class | Legacy number | Extended number |
|---|---|---|---|---|
| `READ_ALIASED` | BOOL | `MBR.nnnn` | 12000+n | 120000+n |
| `READ_ALIASED` | numeric | `MIR.nnnn` | 31000+n | 320000+n |
| `READWRITE_ALIASED` | BOOL | `MBW.nnnn` | 2000+n | 20000+n |
| `READWRITE_ALIASED` | numeric | `MIW.nnnn` | 40250+n | 415000+n |

A coordinate `CC.SS.PP` maps to the memory class by its `BinNumber`
(`0→QBW`, `1→MBW`, `2→IBR`, `3→MBR`, `4→IIR`, `11→MIW`, `13→MBN`,
`14→MIN`, `15→MRN`) and keeps the point number as the address index.
Aliased tags with no alias number (lossy `*_Replicated` exports) are
allocated from the matching class, skipping used indices. Plain unaddressed
tags allocate `MBN` (BOOL), `MIN` (other numeric) or `MRN` (REAL).

### Hardware configuration

- Module points are addressed by their index: `Offset` must equal
  `BoardPoint` and `Size` is 0. Re-sequencing offsets makes TriStation
  report every tag on the module as not allocated (`PC0117`).
- Chassis numbering follows the main chassis type: `8120X_MAIN` uses
  memory −1 / main 0; `EP_MAIN` uses memory 0 / main 1 (ChassisID 9).
- The main chassis carries its main-processor boards and empty-slot
  placeholders; they are taken from the captured chassis catalogue.
- `GlobalMemory.xml`’s `PacketDump` is an LSB-first bitmap per section and
  must mark both `Address` and `PhysicalAddress` of every tag.
- Module boards not present in the catalogue (e.g. 4610 UCM `62`,
  3703 `182`, 3708 `194`/`210`) must be captured as data; otherwise the
  board is emitted as an unknown module (`PC0149`) with no points.

### FBD emission

- A wire running backwards in x (source right of target) is a feedback and
  must carry `feedBack="True"` (`WG0010`).
- Degenerate/zero-length links are stored as direct pin links on **both**
  ends: the target variable has `inConnection`/`inConnectId`, and the feeding
  block pin has `effectiveParam`/`connectId` pointing at the variable.
  Emitting only one side leaves the element disconnected (`WG0012`).
- Block pins are matched by the library port name as well as the export’s
  positional name (`OUT_17` vs `FO_INHIBIT_NEXT`).
- Global declaration types come from the tag table, not from the (often
  typeless) POU interface, otherwise the build fails with `WC0002`.

### Custom FBD diagrams

The FBD path is written against the PLCopen/TriStation model, not against the
sample projects:

- Blocks are resolved from the installed library snapshot plus the POUs defined
  in the same XML; an unknown type is blocked during pre-flight.
- Wires are rebuilt from the export’s geometry (junctions, branches, feedback
  by position), and every emitted connection id is validated after generation —
  a dangling reference is reported instead of silently producing an
  unconnected element.
- PLCopen constructs that have no TriStation representation
  (`connector`, `continuation`, `jump`, `label`, `return`, `action`, `coil`,
  `contact`) are blocked by `check` rather than dropped.
- No corpus-specific names or project ids are special-cased in the generator.

### Source defects found in the corpus

- `33-71-73_FB_Replicated`: 209 referenced globals have no `TAGNAME`
  declaration; TriStation itself fails (`WF0078`).
- `TEMPLATES_DI3`: the shipped `.pt2x` fails `TN0090` on rebuild (six
  duplicate tag addresses), i.e. the export is already inconsistent.
- `PACK_Replicated`: TriStation reports `PC0005` (multiple writes) for both
  its own import and ours; the tag is written by more than one program
  without `EnableMultipleWrite`.
