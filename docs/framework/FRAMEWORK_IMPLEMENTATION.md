# Framework preview — 2026-10-02

This branch combines the 26 CAD tools from `feat/full-toolset` and the four
Ontology/Sion context tools from `main`, then adds nine framework tools.
C# package version: `0.5.0-preview.1`. It is a development preview, not a live-CAD acceptance claim.

## Implemented sequence

1. **Integration**: both histories are merged; all 39 C# MCP tools are registered.
2. **Document binding**: `cad_get_document_identity` returns an ID for one open
   database. `cad_bind_document(document_id)` is required before editing,
   saving or exporting. Each request is checked again inside the plugin's
   document lock. Changing AutoCAD targets clears the binding. Renaming a
   drawing keeps its open-database ID; reopening creates a different ID.
   Semantic candidate contexts also retain this ID and cannot resolve in a
   different drawing even if its handles and entity fingerprints match.
3. **Snapshots**: `cad_extract_snapshot` captures top-level model-space states
   and layer metadata in one read transaction. It reports scanned, returned,
   unsupported and omitted counts. `cad_query_page` pages immutable captured
   data; it does not assert the current drawing is unchanged.
4. **Plans and receipts**: `cad_plan_create` stores a bounded reason, optional
   standard version, document ID, snapshot hash and <=20 steps. Existing targets
   must be present in the snapshot and occur in only one step; fingerprints are
   injected automatically. `cad_plan_execute` defaults to preview and permits
   apply only after successful preview. The plugin rechecks document and target
   state inside one batch transaction. `cad_plan_get` reads the durable receipt.
5. **Review**: `cad_review_snapshot` uses the bundled, versioned ZIUM standard
   without changing its values. It reports unmapped/legacy layers, confirmed ACI
   mismatches and exact duplicate described states. Reports never trigger edits.
   Python `drawing_census` and its CLI provide offline DXF inventories across
   model-space sheets, layouts and block definitions, adapted from PR #3.
6. **Native inventory**: `cad_inventory` (protocol command `drawing_inventory`,
   `ICadDocument.GetDrawingInventory`) lists a live drawing's layouts, block
   definitions, block references and XREFs without the caller naming them. It runs
   in one transaction that opens objects ForRead and never commits. Layouts report
   tab order, model/paper, plot device, media, paper units/rotation/size, viewport
   and entity counts. Block definitions report effective (dynamic parent) name,
   anonymous/layout/XREF/XREF-dependent flags, attribute definitions, entity counts
   by DXF type, nested block counts and insert counts per layout. References report
   handle, block and effective name, position, rotation, scale, layer and attribute
   values per layout, recursing into nested references to `max_depth` (default 2,
   max 8) with `depth`, `path` (handles from the layout down), `block_path` and
   `parent_handle`. XREFs report path, status, `found`, attach/overlay and the nested
   XREF graph from the host database.

## Workflow

1. `cad_list_targets` / `cad_select_target` when several sessions exist.
2. Read `cad_get_document_identity`; bind the returned ID.
3. Capture a snapshot; inspect metadata and page the returned snapshot ID.
4. Review the snapshot and the existing drawing styles before drafting.
5. Create a plan with explicit target objects or new entities.
6. Execute with `dry_run=true`; inspect its changes.
7. Execute with `dry_run=false`; show the receipt and a live `cad_snapshot`.
8. Save a DWG/DXF copy using `cad_save`; original-save confirmation follows the
   existing tool contract.

The usual direct edit tools remain available after document binding. Plans
support `create`, explicit-target `replace_text`, `move`, `delete`, `copy`,
`transform`, `offset`, `set_properties` and `modify_opening`. Broad find/replace,
layer edits, imports and saves are deliberately outside this first plan schema.

## State, bounds and recovery

- Captured snapshots expire after ten minutes or eviction; at most 16 are retained.
- At most 1000 entities and a bounded serialized payload are returned. Scanning
  still visits the whole model space to count and hash it; this is not a streaming
  large-drawing extractor or a full-database hash.
- Layer metadata is also bounded. `resources_truncated` reports omissions.
- Layouts, block definitions, nested instances and XREF contents are excluded from
  native snapshots; `cad_inventory` lists them separately. Unsupported DTOs may only
  describe part of an object's state.
- Inventory limits: `max_blocks` (default 500, max 5000), `max_references`
  (default 2000, max 10000, nested rows included), `max_depth` and the shared
  600,000-byte payload budget used by snapshots. Layout rows are always returned;
  XREF, block and reference rows are dropped in that priority order and reported by
  `xrefs_truncated`, `blocks_truncated`, `references_truncated` and `depth_limited`.
  Counts still cover the whole drawing, so every entity of every block table record
  is opened once; this is not a streaming extractor.
- Inventory does not open XREF files: XREF contents, their blocks and entity
  geometry are excluded. Nested reference positions are in the parent block's
  coordinates, not transformed to the layout. MINSERT and table objects are counted
  by type but not listed as references. Paper-space viewport counts include the
  overall layout viewport and are zero for layouts never activated. The simulator
  exercises the same builder; native AutoCAD behaviour still needs the live gate.
- Plan JSON and receipts live under `%LOCALAPPDATA%\PowerCad\plans`. Plan IDs are
  UUIDs, not arbitrary paths. File replacement is atomic; execution locks are
  shared across processes.
- Prepared/Previewed plans expire after ten minutes. Committed plans return the
  saved receipt when called again. Executing/Indeterminate plans cannot replay;
  inspect live state and reconcile manually before creating a replacement plan.
- Document IDs identify an open database, not a Drive file, source revision or
  canonical Ontology object. Source-ticket resolution remains a separate task.

## Required AutoCAD acceptance gate

The new server must run with the new plugin. Build alone does not update a DLL
already loaded by AutoCAD. Save and close running sessions before installing the
bundle; reopen a drawing copy and test:

- two drawings with equal handles/fingerprints, and a document switch after capture;
- target selection, explicit rebinding and drawing reopen;
- preview rollback, apply, stale-object rejection, locked layers and failed batches;
- one-request Undo grouping, including dry runs and edits after prior user commands;
- dimensions, hatch, offset, measurement, viewport snapshots and DWG/DXF copy save;
- `cad_inventory` on a drawing with dynamic blocks, attributes, nested blocks,
  several layouts and attached, overlaid, nested and missing XREFs;
- semantic context with the actual configured Ontology/Sion service and source data.

Named write locks follow PR #3's Undo-group implementation. Compilation and
simulator tests do not establish that one live UNDO restores the intended batch.

## Remaining development

Local verification on 2026-10-02: 64 .NET tests passed; 61 Python tests passed
and two were skipped. Ruff checks and the complete .NET solution build passed.
The dedicated AutoCAD acceptance attempt stopped before any native check:
AutoCAD did not become COM-ready within 150 seconds. No user drawings were opened.
This is a startup limitation with an undetermined cause, not successful native
acceptance. The installed preview bundle still requires the live gate above.

- Canonical source/revision tickets and source-to-native-object resolution.
- Bounded scanning latency (inventory and snapshots still visit every entity to count it).
- Wall topology, opening-host relationships, dimensions and unit/coordinate tests
  on representative real drawings; open polylines are not automatically defects.
- Capability-derived semantic action choices beyond the current four operations.
- Plan schema for dependent edits to the same object and reviewed layer operations.
- Live acceptance evidence, then release tagging and upgrade instructions.

## Codex registration

After publishing the server, `scripts/register_codex.ps1` registers that executable
with Codex. `-Name power-cad-dev -Simulate` creates a separate simulator entry.
The registration script does not install or reload the AutoCAD plugin.
Reference: https://learn.chatgpt.com/docs/extend/mcp?surface=cli


## Ontology source binding handoff

`cad_bind_document` can optionally accept an `aec-executor-handoff/1` object produced by
the Ontology drawing-context layer. Power CAD verifies that the handoff is
`SOURCE_BOUND`, `VERIFIED_FOR_REVIEW`, explicitly non-authorizing, and bound to the same
live `document_id`. The server stores this provenance with the client binding.

Plans created while such a binding is active persist the source binding. Committed plan
receipts echo `source_binding_handoff_digest`, `source_id`,
`source_byte_revision_id`, and `parser_revision_id` so execution evidence can be joined
back to Ontology without treating the Ontology handoff itself as mutation authorization.
