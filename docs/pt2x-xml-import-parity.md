# XML → PT2X import parity

This note records how the tool reproduces the `.PT2X` that TriStation 1131 5.8 itself
produces when it imports a PLCopen XML export, and what is still not byte-identical.

Reference pair used for the comparison:

| role | file |
|---|---|
| source | `SULFUR1__2.xml` (PLCopen export, 8,718,197 B) |
| TriStation import | `SULFUR1__2.PT2X` (8,126,464 B, project created 2026-10-20 17:37:35, POUs imported 17:41) |
| our build | `pt2conv buildxml SULFUR1__2.xml out.pt2x` (embedded blank base) |

The reference is **not** the original field project: `ProjectInfo` says the project was
created on 2026-10-20 17:37, and the eight POU streams carry import timestamps at 17:41,
so it is a fresh TriStation project whose POUs came from the XML.

## What a TriStation XML import does (learned from the reference)

1. **It starts from a blank project that already contains the whole standard library.**
   The catalog in the reference has 363 library elements (all `.ST`/`.INL`/`.BKG`/`.DOC`,
   DogTags 1..684) plus the eight imported POUs. There are no FBD library elements.
   Therefore a generated FBD POU must be **synthesised** in the catalog; cloning a donor
   (the old behaviour) is impossible and wrong (`LibID`, `Flags`, `Locked`, `ExtOptionFlags`
   all differ).
   The embedded `Resources/base.pt2x.gz` is now a **neutral scaffold captured from a TriStation
   5.8 import on the installed library** (library + sheet templates + hardware, no user POUs).
   Earlier it was the SULFUR1 reference project itself, whose library identities came from an
   older installation; that made TriStation reject regenerated projects with `WB0045` ("Error
   detected while reading file"). The scaffold now carries the installed library layout
   (`LibID` 2/3/4/5, DogTags 4..363) and the current STDSHEET template identities, so every
   generated project refers to library elements the installed archives actually contain.
   A generated POU whose content matches the template keeps the template's exact stream
   (localIds, `network` handles, emission order, timestamps); when the whole project ends up
   unchanged, the template container bytes are reused. User POUs the XML does not define are
   removed from the embedded base (library elements are kept). The extraction is
   cached by content hash, so replacing the resource invalidates the old cache.

2. **Element identity is derived from the import order.**
   - Function blocks are imported in XML order, then programs **in reverse XML order**:
     `FB04_DI=685, CHANGEOVER_SWITCH=686, OUTPUT_TEST=687, TP_RET=688, AI_COND=689,
     P01_FB=690, TEMPLATES=691, SeimensLogic=692`.
   - `FName` is the DogTag in 8 lowercase hex digits (`0x2ad` → `000002ad`).
   - `NextElementDogTag` equals the last used tag (692), not last + 1.
   - Catalog fields are fixed by element kind:
     | kind | ElementType | SubType | ResourceID | Flags | Extension |
     |---|---|---|---|---|---|
     | FBD function block | 2 | 6 | 10162 | 1050655 | .FBD |
     | ST function block | 2 | 6 | 10164 | 1048607 | .ST |
     | program | 1 | 0 | 10162 | 1083423 | .FBD |
     `CategoryID=10097`, `LibID=1`, `ExtOptionFlags=2`, `Locked=False`.
   - ST POUs also get a `.DOC` (`CTS2Doc`) stream; FBD POUs do not.

3. **The project header description is the XML's `fileHeader/@contentDescription`.**

4. **Tagnames.csv** is rebuilt from the XML's `<ts:TAGNAME>` blocks:
   - values are trimmed (TriStation trims trailing spaces and strips `"` from descriptions);
   - columns without an XML source keep the target project's existing per-tag value
     (otherwise `Group1`/`Group2` degrade to one modal value for every tag);
   - `AliasNumberSelection`: `NO_ALIAS`→0, `USER_ALIAS`→2;
   - `AliasType`: `UNALIASED`→1, `READ_ALIASED`→2, `READWRITE_ALIASED`→3;
   - `IsSafety` is true only for `Application="Safety"` (Control is not safety).

5. **FBD streams** (the bulk of the work):
   - **localIds are renumbered per sheet**, in the order dividers → blocks → comments →
     variable placements → wires. Wires consume one id per path segment and keep the last
     id, which is where TriStation's wire-id gaps come from.
   - A normal project stores each FBD diagram **once** (`lastUniqueID` = last used id). A
     second (snapshot) copy offset by the primary span is written only when the source XML
     itself contains the POU twice — that is how TriStation's own importer ends up with a
     two-copy document when it imports an export that already duplicated its POUs. Generating
     a second copy unconditionally makes the editor draw every block twice when opened.
   - Placed variables whose name is not a legal identifier (-empty, `10`, `dir/…`,
     `L%4102`, or shadowing `max`/`min`) are silently dropped by TriStation, together with
     their wires and declarations.
   - Literal placements and zero-length connections are stored as direct pin links
     (`outConnection`/`connectId`), never as wire objects.
   - Every comment of a sheet is written first, in export order (frame comments and notes
     alike); comments are never clipped by the drawing frame.
   - Dividers span the sheet template width (`10 × page width + 18`), are emitted once per
     sheet, and are not duplicated in the second copy.
   - `annotationHeight` is `4` when `annotate=False`, otherwise `height − 2`.
   - Function/function-block boxes are sized from their pin rows: `height = last pin relY +
     pin pitch` (the export can carry the two-input default on a 7-input AND); both input
     and output pins count.
   - A zero-length connection from a literal becomes a direct pin link; from a variable that
     is itself driven by a wire it is dropped entirely (the pin stays bare), matching the
     importer.
   - Snapshots (library pin tables) come from the platform library, not from the template
     project: `Resources/library_snapshots.json` fills the types the built-in table lacks.
     The resource now covers **all 352 library elements** of the 5.8 platform (identity and
     full pin list), generated from `Current/Project.xml` (`DogTag`, `ElementType`,
     `ElementSubType`, `ExtOptionFlags`, `CemEventInputs/Outputs`, the
     `EventConnectionsChanged` event and each element's `Declarations` collection). Without it
     any library type the template never used (`DINT_TO_REAL`, `LIMIT`, `PACK16`, `UNPACK16`,
     ...) was written with `dogTag="0"` and an empty pin list, which TriStation shows as an
     error block.
   - Snapshots for **project-defined** function blocks (a program instantiating `FB04_DI`)
     are built from that POU's catalogue entry (DogTag/ElementType/SubType/flags/event) and its
     interface variables, so the instance resolves. Writing the generic `dogTag="0"` snapshot
     left every instance in the calling programs unresolved.
   - `snapEventId="0"` in a stored block means "no snapshot event", **not** a collapsed box.
     TriStation writes it for blocks drawn in the editor; treating it as collapsed dropped the
     block's pins and made it an error block on import.
   - The element's own catalogue entry must carry its interface in `Collection Name="Declarations"`.
     TriStation compares an instance's snapshot pins with that collection when the project is
     opened; an inserted POU left it empty, so every instance of that type was "out of date"
     and the editor drew a red X across the block. `BuildProjectTypes` now
     rewrites the collection from the same interface pins used for the snapshots.
   - The interface includes the compiler temp pins (`_TEMP_BOOL_n`, `varVTType=4`). TriStation's
     importer writes them and its editor compares them; omitting them also produces the red X.
   - Library block snapshots must use the installed library's element IDs. The captured pin table
     used to carry the older installation's DogTags (`AND=368`) while the project catalogue held
     the installed ones (`AND=9`), so every library block resolved by name but was reported out of
     date. `Resources/library_snapshots.json` is regenerated from the installed catalogue; IDs,
     events and pin tables now match it.
   - A project function block is written in the imported/compiled state: `ExtOptionFlags=3`, the
     `0x6000 | 0x20000 | 0x40` flag bits, `EventCompiled` on the connections event and
     `RefTrigger` pointing at the modification event. Without that state the editor also marks
     the instances out of date.   - A branch arm in the decomposed net that has fewer than two points is not a drawable wire.
     TriStation rejects it as an overlapping segment (`WF0032`). Those arms are now suppressed;
     the block pin is stored as a direct link (`effectiveParam` = source expression,
     `connectId` = source element), which is how the importer stores zero-length links.

### Source defects seen in the M23 export

Opening is clean, but some M23 programs cannot be *built* because the PLCopen export lost data
that is not recoverable from the XML:

| Symptom | Cause |
|---|---|
| `TN0090` duplicate `MBN.xxxx` addresses (215 tags) on TriStation's own import | the export declares one address for many tags; `pt2conv` re-allocates them, TriStation's importer does not |
| `WG0018` unconnected `PACK16` inputs (`P02`, `P03`) | constant placements (literal `0`) were omitted from the export; only the netlist edge survives |
| `WC0140` "Feature not implemented" (`P04`) | the export declares `ADD` blocks over BOOL signals; TriStation has no BOOL addition |
| no execution list | the export carries no `ExecutionList` task; TriStation's import leaves it empty (`WB0237` on build), `pt2conv` schedules every program |

These are reported rather than silently "repaired": inventing logic (for example rewriting
`ADD` to `OR`) would change the safety application.

## Measured parity (SULFUR1 reference, original embedded base)

> This measurement was taken when `base.pt2x.gz` was the SULFUR1 reference project itself.
> The embedded base is now a neutral installed-5.8 scaffold (see above), so a fresh SULFUR1
> conversion no longer reproduces that container byte-for-byte; the FBD/stream parity findings
> below are unchanged.


`pt2conv verify out.pt2x` → 786/786 CRCs OK, OLE container valid. Stream set identical
(1573 streams, no additions/removals). `pt2conv diff` against the reference:

| metric | reference | ours |
|---|---|---|
| streams | 1573 | 1573 |
| byte-identical streams | — | **1573 / 1573** |
| differing streams | — | **none** |
| Tagnames.csv | 459,252 B | **byte-identical** |
| Project.xml | 1,021,821 B | **byte-identical** |
| total payload | 7,463,762 B | **7,463,762 B** |
| container file (SHA-256) | `9B543A48…D50EC8` | **`9B543A48…D50EC8`** |
| FBD content (blocks, vars, wires, comments, pins, geometry) | — | **identical for all 7 POUs** |

The FBD comparison was done by extracting both containers, normalising away `localId` /
`connectId` / `network` / `snapEventId`, and comparing the per-sheet item multisets. Every
POU reports `missing=0 extra=0`: the same blocks with the same sizes, the same variable
placements with the same connections, the same wire geometry and endpoints, the same
comments with the same text and boxes.

Round-tripping both containers through `frompt2` produces decompiled PLCopen XML with
identical element counts: 8 POUs, 1,337 connections, 376 blocks, 1,203 inOut variables,
98 input variables, 786 comments and 205 network dividers.

## Known remaining gaps

These only matter when the XML **differs** from the template project (then the affected POU
is regenerated and the container is re-packed):

1. **`network` handles.** The editor stores an opaque per-object `network="…"` on many
   elements (values such as `901`, `31199`, `17440`). It is not present in the export and
   is not derivable (the same tag on different sheets gets different values). When the POU
   content is unchanged the template's values are preserved byte-for-byte; a regenerated
   POU omits them rather than fabricating values.
2. **Import bookkeeping.** A regenerated FBD uses build-time `dateTime`, extra blank trailing
   sheets are not synthesised (edit history), and localIds/order can differ.
3. **Event/audit fields** in `Project.xml` for a newly inserted element use the build time;
   TriStation's values are a per-import sequence.

For the reference conversion (same XML), the output is **byte-identical** to
`SULFUR1__2.PT2X`, including the OLE container layout.

Wire net decomposition is **done**: junction arms merge through collinear pairs, branches
point at their trunk with `effectiveParam="Vn"`, and the resulting wire set matches the
reference exactly for CHANGEOVER_SWITCH, AI_COND and SeimensLogic. Block pins now carry
`invertedPin`, use the platform library's port names, list unconnected pins, and boxes are
sized from their pin rows.

## Reproduce

```powershell
dotnet build pt2-conv/src/Pt2Conv/Pt2Conv.csproj -c Release
pt2conv buildxml SULFUR1__2.xml out.pt2x
pt2conv verify out.pt2x
pt2conv diff  SULFUR1__2.PT2X out.pt2x
```

`extract` both files and compare the normalized items to see the remaining FBD deltas.

---

# PT2X → XML parity (`frompt2`)

`pt2conv frompt2 SULFUR1__2.PT2X back.xml` reproduces TriStation's own XML export of
the same project. Measured against `SULFUR1__2.xml` (8,718,195 chars):

| metric | reference | ours |
|---|---|---|
| characters | 8,718,195 | 8,357,020 |
| `<pou>` elements | 14 (export repeats definitions) | **8 — one per POU** |
| global tags (`<ts:TAGNAME>`) | 1,021 | **1,021** |
| blocks / input / comment / divider / sheet counts | 434 / 139 / 857 / 206 / 166 | **identical** |
| `<ts:HARDWARE_ALLOCATION>` (attrs: 7,936) | 801,809 B | **identical, byte-for-byte** |
| `<LIBRARY_CONFIGURATION>` | 3,204,143 B | **identical, byte-for-byte** |
| round-trip `back.xml` → `.pt2x` | — | **1573/1573 streams unchanged** |

Checked on a second project (`M23-23 1.xml`, 13 POUs / 799 blocks / 1,021 tags, uses
`DINT_TO_REAL`, `LIMIT`, `PACK16/32`, `UNPACK16/32`): same counts, and every block matches the
professional export by type + position + instance with an identical pin list and box size.

## Reproduced exactly

1. **Line endings.** TriStation writes the XML with LF (inside CDATA too), not CRLF.
2. **POU layout.** One `<pou>` element per program/function block, in project order for the
   function blocks followed by the programs in reverse project order. (TriStation's own export
   repeats every function block and re-emits it after the programs; importing such a file makes
   TriStation merge the two definitions and draw every block twice, so the decompiler does not
   reproduce that artifact. Programs are exported with `version="1.1"`, FBs/functions with
   `version="1.0"`. A POU whose diagram is stored twice keeps a
   `<!-- ts:diagramCopies=2 -->` marker so the XML → PT2X direction still rebuilds the
   two-copy stream.)
3. **Interface.** Variables are classified by the container's `varVTType`
   (0 input, 1 output, 5 global, 3 local); local declarations with `refCount == 1` are
   auto-generated single-use instances and TriStation omits them.
4. **Configuration tags** are split into three `globalVars` blocks: retentive tags
   (`retain="true"`), memory points and I/O points.
5. **Hardware allocation.** The per-module `SETUP_PARAMETERS` /
   `MEMORYALLOCATION_PARAMETERS` descriptors are TriStation module-library defaults, not
   project data. `Resources/hw_module_defaults.json` carries one snapshot per module id;
   the decompiler recomputes the per-instance values: the running
   `InputDiscretes`/`OutputDiscretes`/`InputIntegers` offsets (32 slots per board, class
   specific) and the per-point `PointN.Type` of a 3626X output board (`Not-Used` for points
   that carry a tag, `Non-Supervised` otherwise), plus the empty-slot module (id 96) each
   expansion chassis starts with.
6. **Library blobs.** `LIBRARY_DATA` is a nested compound file holding the compiled library
   (`Current/Bin/*.EMA|NCA`, element streams, `Current/Project`). It ships with TriStation,
   so the three reference libraries are embedded gzipped as `Resources/lib_<NAME>.ole.gz`
   and emitted as single-line base64.
7. **Tag metadata** comes from `Tagnames.csv` (aliases, scaling, display, point/I/O
   parameters, alarm block kind), `HwConfiguration.xml` (bin per slot) and the module
   defaults above.
8. **Duplicated diagram copies.** When a sheet stores each displayable twice, the live copy
   is the one with the lower `localId`; the export keeps it and drops the snapshot.

## Known remaining gaps (not recoverable from the `.pt2x`)

1. **Placements TriStation itself discarded on import.** 78 variable placements (and their
   80 wires) in `SeimensLogic` carry names that are not legal identifiers
   (`direg01/DI01…`, `xsreg01/…`, `max`, `min`, `TRUE`, `10`, …); TriStation dropped them
   when it imported the XML, so nothing in the container refers to them. This is the bulk
   of the remaining size delta (plus their declarations).
2. **Trimmed tag strings.** `Tagnames.csv` stores `Group1`/`Description` without the
   trailing space the XML export shows (1,004 group names, 542 descriptions). The container
   keeps no untrimmed copy.
3. **Physical bin allocation.** For memory areas whose CSV `Address` is the sequential
   logical address (`MBN.0001`), the export's `PhysicalAddress`/`BinNumber`/`BinOffset`
   reflect the allocator's bit map in `GlobalMemory.xml`. Rebuilding that map (648 tags)
   is possible but not implemented.
4. **Displayable order inside a sheet** occasionally differs from the container order
   (a block listed before the variables it follows here). The element set, geometry,
   connections and ids are identical.
