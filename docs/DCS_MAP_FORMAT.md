# DCS F10 vector map — format notes

Reverse engineered on 2026-10-20 from the user's own DCS install (Caucasus, Syria), for
local interoperability. Decoder: `tools/dcs_vectormap.py`. Not documented by ED; may change
with DCS updates.

## Files (`Mods/terrains/<id>/Map/`)
- `<id>.sup5` — vector lines of the F10 "map" mode (landscape5 scene).
- `<id>.gn4` — place names (`landscape4::lGeoNamesFile`, double positions) — not decoded yet;
  `towns.lua` in the same folder already gives names with lat/lon.
- The coloured fills of F10 map mode (water, forest, built-up) are **not** in these files;
  they come from the terrain mesh (`surface/<id>.surface5`, see below) drawn with the map
  palette. Satellite mode is the terrain's colour clipmaps (see "Satellite clipmaps").

## sup5 layout
- Header 48 bytes; `u64` at offset 8 = **data-block base** (index and vertex buffers).
  Everything before it is an entry stream; buffer offsets are relative to the base.
- **Node**: 4×4 float matrix, identity rotation, translation in elements 12..14 = tile
  origin (16 384 m grid), followed by a 6-float local bbox. Applies to following vertices.
- **Vertices**: `u32 1, u32 1, 50 02 00 0c, 5×00, u64 bytes, u32 offset` → float32
  `(x, y, z)` triples local to the current node (x north, z east, y = 0).
- **Geometry**: `u32 8, "LINELIST", u16 fmt, u16 stride, u32 1, u64 index_bytes,
  u64 index_offset, u16 0, u32 n, strings…` — strings are the shader (`Map4.0`) and the
  **class**; params `colorIndex` (byte) and `shaderDefine` (`NODEFINITIONS` or
  `VIEW_IN_MAPTEX`). Indices are `u32` pairs into the **most recent vertices entry before
  this geometry** (the pairing is shifted by one against the visual record boundaries —
  the same shift as in `RasterCharts/rasterCharts.sup5`).

## Classes seen
| Class | Caucasus | Syria | Meaning |
|---|---|---|---|
| `Road` | 2 852 groups | 6 468 | roads |
| `River` | 2 590 | 2 339 | rivers |
| `ELT` | 1 264 | 1 814 | power lines |
| `Rail` | — | 509 | railways |
| `MGRS` | 76 129 | 48 617 | MGRS grid |
| `Line` | 46 877 | — | lat/lon graticule |
| `Undef` | 760 | 675 | unknown (few lines) |

## Road network (`roads/<id>.rn4`, "landscape4::lRoadNetwork")
Decoder: `client/Dcb.Client.Core/RoadNetwork.cs` (probe: `Dcb.Probe --roads <file>`).
- Header: u32 2, u32 48, u64 …, class name (length-prefixed), u32 5, u32 count, then
  `count` length-prefixed **type names** (Syria: 23 — track, secondary, primary, motorway*,
  link, viaduct_*, *_slope, *_tunnel, rail_main, rail_branch …).
- **Edge**: `u32 6` (constant tag), `u8 kind` (1 rail, 2 road), `u8 type index`,
  `u32 node`, `u32 node`, `u32 0`, `u32 n`, then n × (x, y, z) float64 (DCS metres, y =
  elevation); then `u32 n` + n × float64 (x, y, z) unit directions; other tables (lanes,
  lengths, spatial index) lie between edges, so edges are found by that signature.
  (Reading the leading 6 as the point count finds only the 6-point edges — a tenth.)
- Syria: 646 311 edges, 10.6 M points, ≈ 423 000 km (294 000 km tracks), decoded in ~2 s;
  Douglas–Peucker at 2.5 m → 4.1 M points; cached as `%LocalAppData%\DCB\cache\roads_*.bin`.

## F10 map-mode palette (`vfsTextures/Map.zip: mapmodepalette.tif`, 64×512)
Left 32 columns: height ramp, row 0 white (high) → row 511 pale tan (low). Right 32
columns: 16 class swatches of 32 rows: 0 #CCCC99, 1 #939083, 2 #59574A, 3 #364E15 (α132),
4 #DC9C48, 5 #9F2616, 6 #378288, 7 black (α93), 8 #08197F (α138, water), 9 black,
10 #374F15 (α216), 11–12 black, 13 red, 14 white, 15 transparent. The height scale of the
ramp is not in the file (DCB uses 0–5000 m linear, uncalibrated).
Caucasus and Persian Gulf have no `Map.zip`: the palette is `vfsTextures/MapTextures.zip:
mapmodepalette.tif.dds`, DXT5 (BC3), 64×512, 10 mips, same layout — but the swatches are
ordered differently: water (translucent blue, α130) is class **9**, class 8 is opaque black,
class 0 brown #99572B. DCB picks water by colour (2026-10-23).

## Terrain surface (`surface/<id>.surface5`, "landscape5::Surface5File", 29 GB)
The terrain mesh, in the landscape5 container. Geometry groups carry a class — `Land`,
`Road`, `field`, `Town`, `Trees`, `River`, `Sea` … — plus a per-triangle `TRITYPE`: the
source of the F10 map's area fills. Decoded for the area classes (2026-10-21); extractor
`client/Dcb.Client.Core/LandCover.cs` (`Dcb.Probe --landcover …` writes a PNG), record
lister `tools/dcs_surface5_probe.py`:
- Header u64 at 8 = data base (Syria 273 400 528); entry stream before it, buffers after.
- **Record**: `u32 2`, length-prefixed shader (`Surface5.0`, `HardSplat5.2`, `Splatmap5.1`,
  `Simple5.1`, `Sea5.1`, `Lake5.1`, `MeshAssetManifold5`), length-prefixed class, then
  `u32 0, u32 layer, u32 ?, …`, a local bbox (6 floats), vertex count, attribute count.
  `layer` is constant per class (Town 95–99, field 82–87, Trees 93, lake 102, Sea 109).
  First 64 MB: Road 17 301, field 7 068, Land 6 856, Town 6 516, Sea 2 978, River 2 397,
  Trees 2 065, Beach 978, Rail 899, lake/Lake 1 691, Building 147.
- **Attribute**: `u32 1`, length-prefixed name, `u16 type, u16 element size, u32 flag`,
  `u64 byte count` (**bytes**, not elements), 48-bit data offset from the data base.
  `TRI` = u32×3 per triangle; `Pbase` = float32×3 positions (local); `baseIndex` = u32.
- **The arrays found after record k's header belong to record k+1's class** — the same
  one-off shift as the F10 sup5 placements. (Checked: the vertices of the arrays after record
  85 fill record 86's Town bbox to the metre; likewise 86→87 lake, 89→90 field, 102→103.)
- **Nodes**: one identity-rotation 4×4 matrix per node, translation at elements 12/14 =
  node centre (x, z), followed by the node's bbox (6 floats: min x, y, z, max x, y, z).
  A node's class records come first, each with `baseIndex` + `TRI`; then the span holding
  **`Pbase`, the shared vertex pool** (its own `TRI` is the plain terrain). Every
  `baseIndex` in the group is < that pool's vertex count (e.g. records 74–90: max 6 553,
  pool 6 554).
- **The matrix found in a pool span belongs to the NEXT pool** (the header precedes its
  data, again one off): the bbox after the matrix in pool span k matches the vertex extent of
  pool span k+1 (3 299 of the first 4 000 pools exactly, none their own). World x/z = pool
  vertex (x, z) + the previous pool span's translation. Using the same span's matrix puts
  every node on its neighbour, mostly 8 km east (fixed 2026-10-21; it looked like sea
  under the coast, lakes and forests repeated eastwards, Damascus without a town).
- **Levels of detail**: besides the 8 km nodes (pool |x| ≤ ~7.7 km including skirts) the file
  holds coarser nodes 16 km to 512 km across (|x| ≥ 8.2 km; Syria 16 231 fine nodes,
  2 315 / 743 / 86 / 38 / 10 / 4 coarser) repeating the areas with simpler outlines. DCB
  rasterises the finest only. **The finest size differs per terrain**: node widths (bbox
  after each matrix) cluster Syria 8–14 km, then 17–28 km…; Caucasus 17–26 km, then 29 km…
  (Caucasus has no 8 km level). `Surface5.FinestWidth` takes the narrowest 1 % and stops at the
  first gap of > 1 km between sorted widths (Syria land cover: 75 of 75.6 M cells changed
  against the old fixed |x| < 8 km rule; pavement identical). The per-vertex `depth` attribute
  is not a level number.
- Triangle vertex k of a class record = `Pbase[baseIndex[TRI[k]]]`.
- Check against DCS (server-sampled 500 m surface grid, after the fix): lake cells on DCS
  land 988 (outline fringe; 33 674 before), sea on land 152, trees/town/field on water ~900.
- ~11 s to extract at 100 m (the data reads dominate), 2.5 MB cached.
- Roads/rivers/rail records are left to the vector layers.
- **Class names and shaders differ per terrain** (`Dcb.Probe --census <surface5>`, 2026-10-23).
  Syria: `Trees` Simple5.1; `field` HardSplat5.2 + `Field` Splatmap5.1; `Town` HardSplat5.2,
  Blend5.1, Splatmap5.1; `lake` Lake5.1 + `Lake` Splatmap5.1; `Sea` Sea5.1 + WaveHeightOnlay5.1;
  `Beach` Splatmap5.1. Caucasus: **`Forest`** CaucasusSimple5.1; `Field` Blend5.1,
  SimpleOnlayArray5.1, HardSplat5.1; `Town` HardSplatArray5.1; `lake` Lake5.1; `Sea` Sea5.0 +
  WaveHeightOnlay5.1; `Land` Surface5.0 / CaucasusSimple5.1 (plain terrain); `Beach` also on
  Road5.1/River5.1/BreakingWaves4.0 records. The extractor reads **every shader's records**
  (span scan without a shader list) and maps `Forest` like `Trees`. Before, only 7 Syria shaders
  started a span, so Caucasus got no area fills at all, and on Syria a skipped shader's record
  was folded into the previous span (only its first `baseIndex`/`TRI` were seen): Syria now
  +735 km² trees, +293 sea (coastal wave onlay), +142 field, −21 town, −6 lake of 755 000 km².
  Caucasus around Kutaisi (50 × 60 km): 867 km² forest, 257 field, 1 326 town (village splats
  cover rural settlements widely), 49 lake; whole theatre extracts in ~3–4 s at 100 m (1.5 MB).

## Airfield pavement (same file, class `Runway`)
Runways, taxiways, aprons, shelters and stands are their own meshes in the surface5 file: class
`Runway` with shaders `Runway5.1` (the geometry; also `RunwayOnlay5.1`, `OnlayMarks5.1` for the
painted markings, not used). Same record and pool rules as the area classes. The `layer` value
differs per field (Akrotiri 163/167, Damascus 142/143, Incirlik 211–213, Bassel 201): one layer
is the paved area, the other a thin blending strip along its edges — the union of all
`Runway5.1` triangles of the finest nodes is the pavement. Older fields (Hama, Damascus) model
the taxiways and aprons but the runway surface itself only as an edge strip, so runways are
drawn from `Airbase:getRunways()`. **Caucasus** uses other shaders for the same class:
`OldRunway5.1` (477 records, the pavement), `Runway5.0` (4), `CaucasusSimple5.1` (coarse, left
out), onlays `OldRunwayOnlay5.1` / `RunwayOnlay5.0`; 68 953 triangles, 0.1 s (2026-10-23).
Syria: 742 990 triangles, extracted in 0.8 s
(`Core/Pavement.cs`, `Dcb.Probe --pavement`), cached as `pavement_*.bin` (5.4 MB). The span
scan finds records of every shader here (the area extractor keeps its 7-shader list; switching
it to the shared reader `Core/Surface5.cs` changed none of Syria's 75.6 M land-cover cells).

## Scene objects (`Scenes/<id>.scn5`, "landscape5::Scene5File")
Every placed object of the terrain (buildings, lights, barrels, signs…). Syria 5.5 GB; header
u64 at 8 = data base (106 MB entry stream).
- **Model names**: length-prefixed strings from offset 66 (Syria 6 433). An instance's
  `TYPE_OPTIONS` low 16 bits index this table.
- **Nodes**: 4×4 **double** matrix, identity rotation (1.0 at elements 0, 5, 10), translation
  x, y, z at 12..14 (grid tiles, 512 m and up), then a world bounding box (6 floats).
  Syria 261 837 nodes. (Unlike surface5, a node's matrix belongs to its own blocks.)
- **Instance block** after it: `u32 2, u32 count, u32 4` (attributes), then `u32 1, u32 1,
  50 02 00 0c`, 5 zero bytes, u64 byte count, u48 data offset = positions (float32 x, y, z,
  node-local; y = elevation ASL); then named attributes as in surface5 (`u32 1`, name, u16 type,
  u16 size, u32 flag, u64 bytes, u48 offset): `TYPE_OPTIONS` (u32 per instance), `SEED_SCALE`,
  `AXIS` (float3). A node holds several blocks.
- **Airfield signs** (`Models/AirfieldsTabs`): models named after their face — `<x>_black`
  location sign (on taxiway X), `yellow_<x>` direction sign, `course_<d>`/`course_l|r|dash`
  runway sign, `arrow_*`. Boards are panels 0.8 m apart: a letter and a digit panel together
  name a taxiway like "A1". Syria: 6 904 signs at 40 airdromes (`Core/AirfieldSigns.cs`,
  `Dcb.Probe --signs`; index 0.1 s, one field 5 ms).
- **Caucasus** (615 models, 25 306 nodes): location boards `<x>_block`, direction boards
  `<x>_left|_right|_frwd` (letters A–J), runway signs `course_31_13` / `course_31l_13r`, towers
  `kdp`, `kdp_big`, `batumi_kdp`, `airbase_*kdp*_tbilisi`. 266 signs; letter boards only at
  Batumi, Senaki, Sukhumi, Tbilisi-Lochini, Soganlug, Vaziani — none at Kobuleti, Kutaisi and
  the rest (DCS data, not a reader gap). **Nodes of different sizes**: signs in 512 m tiles,
  towers in 16 km tiles (objects up to 8 km from the translation), and one node near the
  origin (translation (−216, 551)) holding Sukhumi's signs at map-wide offsets. A node's reach
  is taken from its translation's grid (largest power of two dividing it, ×1.25, ≥ 1 km;
  off-grid = always searched); this also finds Nevatim's tower on Syria, missed before.
- Airfield taxi networks (`AirfieldsTaxiways/*.rn4`) on Caucasus are not named like the fields
  (`Senaki`, `Lochini`, `MinVody`, `Nalchick`, `Krasnodar_P`, `TbilisiMilitary` for Soganlug):
  DCB falls back to the file whose network extent holds the runway centre.
## Raster charts (same container, `RasterCharts/rasterCharts.sup5`)
Each tile record's placement (matrix + bbox) belongs to the **next** texture name in the
file. See `client/Dcb.Client/Map/Charts/ChartIndex.cs`.

## Land textures (`vfstextures/LandTextures<Season>.zip`, Caucasus; 2026-10-23)
Terrains without `clipmaps` (Caucasus) keep the ground colour — what F10 "Sat" shows — as a
virtual texture: `VirtualTextures/colortexture.vti` ("lVirtualTextureIndexFile4", index range
−66…32 × −66…97) names the tiles, and `LandTextures{Summer,Spring,Autumn,Winter}.zip` hold them
(~520 MB each): `land_39_<i>_<j>.png.dds` (11 808) and a 4× coarser `land_156_*` (871), 256×256
DXT5 (alpha not coverage). The level names are nominal: the mesh's `xyz2uvw` per node is only its
translation (the per-vertex `parametricUVW` is zero), so DCS maps **world position** straight to
the texture: **38.4 m/px from the map origin**, tile (i, j) spanning x ∈ [i·9 830.4, (i+1)·9 830.4)
north and z likewise east, pixel columns along +x, rows along +z (the clipmaps' layout). Measured:
a least-squares fit over all 21 runways gave 38.43 / 38.39 m/px, intercepts 0.1 / 0.4 px, rotation
0.02°, 6.5 px rms (the runway detector's noise); DCS's 500 m water cells then trace the coast at
both ends of the map (Batumi–Poti, Anapa–Gelendzhik). An early 39.06 m/px guess (10 km tiles)
fitted the south only — off ~3 km at Anapa. The texture paints some airfields slightly off
(Kobuleti ~100 m south-east of DCS's runway; Anapa exact). `Map/Satellite/LandTextures.cs`;
season from the mission date (Dec–Feb winter, Mar–May spring, Jun–Aug summer, Sep–Nov autumn).

## Town ground (F10 "Sat" detail; research 2026-10-23, not built)
The sharp villages/towns of F10 Sat on Caucasus are the `Town` records (`HardSplatArray5.1`) of
`surface5`, textured per triangle with a village photo. **DCS describes the materials in plain
Lua: `Mods/terrains/<id>/misc/materials.lma.lua`** (streams, defines, texture slots per shader);
`misc/assets.assets` ("lAssetFile2.0", 17.8 MB) describes every asset, `misc/assets.ofsh` the
source scenes, `misc/shaders.json` the shader list. No terrain shader source ships
(`Bazar/shaders` has none of these defines).
- `HardSplatArray5.1` (materials.lma.lua l.1024): streams `parametricUVW` = **INT1 into a shared
  buffer `MeshAssetManifold5`** (`xyz2uvw` FLOAT16, `tangent`, `binormal` FLOAT4; l.680),
  `offset`, `assetIndex` (per primitive), and texture channels `main` (color5), `closeupR/G/B/A`,
  `closeup0`, `overlay`, each with **`setMaterialManifold` 0…6** — a second, per-material
  transform from uvw to that texture's UV. Textures per asset: `<asset>.color.tif` (the photo,
  `CLAMP_MAINTEXTURES`), `.roofs.tif`, `<asset>.tif` (splat map driving the close-up textures),
  `.nm.tif`, `.si.tif`; `colorIndex`, `overlaySplatmapMask` properties.
- **Found in the file:** the `MeshAssetManifold5` buffer of a node is the `xyz2uvw` / `tangent` /
  `binormal` arrays in the node's **pool span** (the span holding `Pbase`); a Town vertex's
  `parametricUVW` (u32) indexes it (node (−350208, 613376): 121 matrices, town indices 0–98; all
  sampled nodes consistent). The matrix is **row-major on the node-local vertex position**:
  rows 0 and 2 a rotation in the ground plane plus a shift (metres), row 1 the height. Per-vertex
  `materialParamsIndex` (1015–1034 for towns) matches the asset record's `materialIndex` in
  assets.assets (village_1: 1019).
- **Not yet found:** the per-material manifold transform (uvw → photo UV). Plain wrapping by
  1 024 m, all 8 axis orientations, scored against an F10 screenshot of Kobuleti: no correlation
  (NCC ≈ 0.01). uvw ranges per asset span 3–22 km across its instances, so uvw is not
  asset-local metres; the photo's frame must come from the material manifold (likely in the
  material table ~13.31 MB of surface5 or the asset record in assets.assets, which also lists
  the asset's sub-meshes: `gazin_new`, `home1_b`, `domik1c` with transforms).
- Village photos: `village_N.color.tif` 1024², ~1 m/px by house size.
- Tools: `tools/research/caucasus/town_rec.py`, `manifold_fit.py`, `manifold_apply.py`,
  `asset_uv.py`; prototype `tools/SatPrototype` (towns placed by manifold, `score` command:
  shift-searched NCC against a reference).

## Satellite clipmaps (`clipmaps/colortexture/<R>m[(season)]/<R>m[x]<AA><col>.tif.clipmap`)
The terrain's satellite ground texture (also `normalmap/`, `splatmap/` in the same format).

**Naming / georeference.** Block = 8192 px at R m/px (S = 8192·R metres), on the same grid as
the raster charts. Letters = row along DCS x: AA = 0, AB = 1 … BA = 26; an `x`/`X` prefix
makes it negative. Number = column along DCS z (signed). Block spans x ∈ [row·S, (row+1)·S],
z ∈ [col·S, (col+1)·S]. (Akrotiri x −35297, z −270072 → 1 m block `xAF-33`, present.)

**Header** (little-endian): u32 version = 3, f32 m/px, u32 tile px = 256, u32 n + n chars
format (`bc3`), u8 (1), u32 level count (6: 32², 16², … 1² tiles).
Per level: u32 1, u64 0, u32 tile px, u32 side, then `side` runs of: u32 count, count ×
(u32 present, u64 file offset). **Run k = the tiles k steps north of the block's south edge,
west → east.**

**Tile** at offset: u32 length, zlib stream → 65 536 bytes = raw BC3 (DXT5) 256×256, no mips.
The pixels are **not north-up: image columns run north (+x), image rows run east (+z)**;
the tile's image origin is its south-west corner. Alpha is always 255.

**No imagery**: solid RGB565 0x8B8B (140, 113, 90); coarser levels re-encode it a step off
(e.g. 0x8BAB), and blocks at the imagery's edge blend it with one image endpoint. Under the sea
the texture is seabed (DCS draws water over it).

How this was established: assembling level 4's 2×2 in run order reproduces level 5 (mean diff
2.4 vs ≥ 9 for other orders); the pixel orientation is the only one of 8 that correlates with
DCS's land.getSurfaceType water mask (0.82; others ≤ 0.08, block `4mxAC-9`).
