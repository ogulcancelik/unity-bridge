# Command Reference

Full reference for every endpoint and command. For a quick overview, see the [README](../README.md).

AI agents don't need this — they get everything from `GET /api` at runtime. This is for humans who want to browse.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | Status, Unity version, project name |
| GET | `/api` | Self-describing schema (for AI agents) |
| GET | `/events?since=<cursor>&limit=<n>` | Poll events |
| POST | `/command` | Execute a command |

## Conventions

### Target Resolution

Commands that take `target` accept:
- **Name**: `"Player"` — first match in scene
- **Path**: `"Environment/Props/Barrel"` — hierarchy path
- **Instance ID**: `"-12345"` — exact Unity instance ID (returned by create/find commands)

### Object References (`$ref`)

For properties that hold Unity objects (materials, sprites, prefabs, audio clips, etc.), use the `$ref` envelope:

```json
{"$ref": {"kind": "asset_path", "value": "Assets/Materials/Red.mat"}}
{"$ref": {"kind": "guid", "value": "abc123def456"}}
{"$ref": {"kind": "instance_id", "value": "12345"}}
```

`get_property` returns the same format when reading object references, including the resolved `type` and `name`.

### Type Conversion

`set_property` auto-converts values:
- `[x,y,z]` → Vector3
- `[x,y]` → Vector2
- `[r,g,b,a]` → Color (0-255 range auto-detected)
- `[x,y,z,w]` → Quaternion
- strings → enums
- `{"$ref":{...}}` → UnityEngine.Object references

---

## Scene

### get_hierarchy

Scene objects as a flat list. Supports multiple detail levels and filtering.

```bash
# Compact (default)
curl localhost:7778/command -d '{"type":"get_hierarchy"}'

# With more detail
curl localhost:7778/command -d '{"type":"get_hierarchy","params":{"view":"standard"}}'

# Full detail with filtering
curl localhost:7778/command -d '{"type":"get_hierarchy","params":{"view":"full","root":"Environment","filter":{"has_component":"MeshRenderer"},"depth":3}}'
```

**Params**: `view?` (summary|standard|full), `root?` (target), `depth?` (int, default 10), `filter?` ({name_contains?, has_component?}), `limit?` (int, default 1000)

**Views**:
- **summary** (default): `id, n(name), a(active), p(parent_id), d(depth), cc(child_count)`
- **standard**: + `tag, layer, pos`
- **full**: + `rot, scl, comp[]`

### get_scene_info

```bash
curl localhost:7778/command -d '{"type":"get_scene_info"}'
```

Returns scene name, path, dirty state, root count, loaded status.

### load_scene

```bash
curl localhost:7778/command -d '{"type":"load_scene","params":{"path":"Assets/Scenes/Main.unity"}}'
```

### save_scene

```bash
curl localhost:7778/command -d '{"type":"save_scene"}'
```

---

## GameObjects

### create_gameobject

```bash
curl localhost:7778/command -d '{
  "type":"create_gameobject",
  "params":{"name":"Player","primitive_type":"Capsule","position":[0,1,0],"components":["Rigidbody"]}
}'
```

**Params**: `name` (string), `primitive_type?` (Cube|Sphere|Capsule|Cylinder|Plane|Quad), `position?` ([x,y,z]), `rotation?` ([x,y,z]), `scale?` ([x,y,z]), `parent?` (target), `components?` (["Rigidbody", ...])

**Returns**: `{id, name}`

### find_gameobjects

```bash
curl localhost:7778/command -d '{"type":"find_gameobjects","params":{"search":"Player"}}'
curl localhost:7778/command -d '{"type":"find_gameobjects","params":{"search":"Enemy","method":"by_tag"}}'
curl localhost:7778/command -d '{"type":"find_gameobjects","params":{"search":"MeshRenderer","method":"by_component"}}'
```

**Params**: `search` (string), `method?` (by_name|by_tag|by_layer|by_component), `include_inactive?` (bool)

**Returns**: `results[]: {id, name, path}`

### modify_gameobject

```bash
curl localhost:7778/command -d '{
  "type":"modify_gameobject",
  "params":{"target":"Player","position":[10,0,0],"rotation":[0,90,0],"active":false}
}'
```

**Params**: `target` (target), `name?` (string), `position?` ([x,y,z]), `rotation?` ([x,y,z]), `scale?` ([x,y,z]), `parent?` (target, "" to unparent), `tag?` (string), `layer?` (string), `active?` (bool)

### duplicate_gameobject

```bash
curl localhost:7778/command -d '{"type":"duplicate_gameobject","params":{"target":"Player","name":"Player2"}}'
```

**Params**: `target` (target), `name?` (string), `position?` ([x,y,z])

### delete_gameobject

```bash
curl localhost:7778/command -d '{"type":"delete_gameobject","params":{"target":"Player2"}}'
```

---

## Components & Properties

### add_component

```bash
curl localhost:7778/command -d '{"type":"add_component","params":{"target":"Player","component_type":"Rigidbody"}}'
```

### remove_component

```bash
curl localhost:7778/command -d '{"type":"remove_component","params":{"target":"Player","component_type":"Rigidbody"}}'
```

### get_components

```bash
# Basic listing
curl localhost:7778/command -d '{"type":"get_components","params":{"target":"Player"}}'

# With all property values
curl localhost:7778/command -d '{"type":"get_components","params":{"target":"Player","detailed":true}}'
```

### set_property

Uses reflection to set **any** property on **any** component — public properties, public fields, and `[SerializeField]` private fields.

```bash
# Scalar
curl localhost:7778/command -d '{"type":"set_property","params":{"target":"Player","component_type":"Rigidbody","property":"mass","value":10}}'

# Vector
curl localhost:7778/command -d '{"type":"set_property","params":{"target":"Player","component_type":"Transform","property":"localScale","value":[2,2,2]}}'

# Object reference (material)
curl localhost:7778/command -d '{"type":"set_property","params":{"target":"Player","component_type":"MeshRenderer","property":"sharedMaterial","value":{"$ref":{"kind":"asset_path","value":"Assets/Materials/Red.mat"}}}}'
```

### get_property

```bash
curl localhost:7778/command -d '{"type":"get_property","params":{"target":"Player","component_type":"Rigidbody","property":"mass"}}'
```

Object references return the `$ref` envelope format with resolved `type` and `name`.

---

## Editor Control

### editor_state

```bash
curl localhost:7778/command -d '{"type":"editor_state"}'
```

Returns: play mode, pause state, compiling, platform, Unity version, project path, scene name/path.

### play / stop / pause

```bash
curl localhost:7778/command -d '{"type":"play"}'
curl localhost:7778/command -d '{"type":"stop"}'
curl localhost:7778/command -d '{"type":"pause"}'
```

`pause` toggles — call it again to unpause.

### refresh

Refreshes the AssetDatabase. **Deferred**: holds the HTTP connection open until compilation/import finishes, then responds.

```bash
curl localhost:7778/command -d '{"type":"refresh"}'
```

If scripts triggered a domain reload, the response includes `"domain_reload": true` — the bridge will restart automatically, reconnect via `/health`.

### read_console

```bash
curl localhost:7778/command -d '{"type":"read_console","params":{"count":10}}'
```

### execute_menu_item

```bash
curl localhost:7778/command -d '{"type":"execute_menu_item","params":{"menu_path":"File/Save Project"}}'
```

---

## Events

Poll for console logs, compilation status, play mode changes, and scene events.

```bash
# Start polling (since=0 gets recent events)
curl localhost:7778/events?since=0&limit=50

# Continue from last cursor
curl localhost:7778/events?since=42&limit=50
```

Event types: `log` (with severity: info/warning/error), `compile_start`, `compile_end`, `play_mode`, `scene_change`.

---

## Assets

### find_assets

```bash
curl localhost:7778/command -d '{"type":"find_assets","params":{"filter":"t:Material","max_results":10}}'
```

**Params**: `filter` (string — e.g. `t:Material`, `t:Prefab`, `player`), `search_in?` (string, default "Assets"), `max_results?` (int, default 50)

### create_asset

```bash
curl localhost:7778/command -d '{"type":"create_asset","params":{"path":"Assets/Materials/Red.mat","asset_type":"material"}}'
curl localhost:7778/command -d '{"type":"create_asset","params":{"path":"Assets/Prefabs","asset_type":"folder"}}'
```

**Supported types**: `material`, `folder`

---

## Screenshots & Capture

### screenshot

Captures the scene view camera to a PNG file.

```bash
curl localhost:7778/command -d '{"type":"screenshot"}'
curl localhost:7778/command -d '{"type":"screenshot","params":{"filename":"shot.png","width":2560,"height":1440}}'
```

**Params**: `filename?` (default "screenshot.png"), `path?` (output dir, default "Assets"), `width?` (default 1920), `height?` (default 1080)

### capture

Debug screenshot with a custom camera. Saves to `_debug/` directory. Supports preset camera modes or manual positioning.

```bash
# Top-down view
curl localhost:7778/command -d '{"type":"capture","params":{"mode":"topdown","center":[0,0],"size":100}}'

# Isometric
curl localhost:7778/command -d '{"type":"capture","params":{"mode":"isometric","center":[0,0],"size":80}}'

# Front view
curl localhost:7778/command -d '{"type":"capture","params":{"mode":"front","center":[0,0],"size":50}}'

# Manual camera
curl localhost:7778/command -d '{"type":"capture","params":{"position":[10,20,10],"rotation":[45,0,0],"ortho":true,"ortho_size":30}}'
```

**Params**: `mode?` (topdown|isometric|front), `center?` ([x,z]), `size?` (float, default 80), `position?` ([x,y,z]), `rotation?` ([x,y,z]), `ortho?` (bool), `ortho_size?` (float), `fov?` (float, default 60), `width?` (default 1920), `height?` (default 1080), `filename?` (default "capture.png")

### capture_sequence

Multi-step screenshot sequence. Pause the game, step frames, capture, resume — all in one atomic command. **Deferred**: holds the HTTP connection open until the sequence completes.

```bash
curl localhost:7778/command -d '{
  "type":"capture_sequence",
  "params":{
    "source":"game",
    "steps":[
      {"action":"pause"},
      {"action":"wait","seconds":0.1},
      {"action":"capture","filename":"frame1.png"},
      {"action":"step","frames":5},
      {"action":"capture","filename":"frame2.png"},
      {"action":"resume"}
    ]
  }
}'
```

**Sources**: `game` (full Game View, play mode only), `scene` (Scene View camera), `main` (Camera.main render), `custom` (temp camera at position/rotation)

**Step actions**: `pause`, `resume`, `wait` ({seconds}), `step` ({frames?}), `capture` ({filename?})

**Returns**: `{captures:[{filename, path}], duration_ms}`

---

## Selection

### get_selection

```bash
curl localhost:7778/command -d '{"type":"get_selection"}'
```

Returns the active GameObject and all selected GameObjects.

### set_selection

```bash
curl localhost:7778/command -d '{"type":"set_selection","params":{"target":"Player"}}'
```

Selects the object and pings it in the hierarchy.

---

## Project

### get_project_info

```bash
curl localhost:7778/command -d '{"type":"get_project_info"}'
```

Returns: project name, Unity version, render pipeline (BuiltIn/URP/HDRP), platform, paths, play/compile state.

### get_tags / get_layers

```bash
curl localhost:7778/command -d '{"type":"get_tags"}'
curl localhost:7778/command -d '{"type":"get_layers"}'
```

### add_tag / add_layer

```bash
curl localhost:7778/command -d '{"type":"add_tag","params":{"tag":"Enemy"}}'
curl localhost:7778/command -d '{"type":"add_layer","params":{"layer":"Projectiles"}}'
```

`add_layer` uses the first empty slot (indices 8-31).

---

## Prefabs

### create_prefab

Save a scene object as a prefab asset.

```bash
curl localhost:7778/command -d '{"type":"create_prefab","params":{"target":"Player","path":"Assets/Prefabs/Player.prefab"}}'
```

### instantiate_prefab

Spawn a prefab into the scene.

```bash
curl localhost:7778/command -d '{"type":"instantiate_prefab","params":{"path":"Assets/Prefabs/Player.prefab","position":[5,0,0]}}'
```

**Params**: `path` (prefab asset path), `position?` ([x,y,z]), `rotation?` ([x,y,z]), `scale?` ([x,y,z])

---

## Batch

Execute multiple commands in a single HTTP request.

```bash
curl localhost:7778/command -d '{
  "type":"batch",
  "params":{
    "commands":[
      {"type":"create_gameobject","params":{"name":"A","primitive_type":"Cube","position":[0,0,0]}},
      {"type":"create_gameobject","params":{"name":"B","primitive_type":"Sphere","position":[3,0,0]}},
      {"type":"modify_gameobject","params":{"target":"A","rotation":[0,45,0]}}
    ]
  }
}'
```

Returns `{results: [...]}` — one result per command, in order.

> **Note**: `refresh` cannot be used inside batch (it defers the response). Call it as a separate command.

---

## Escape Hatch

### execute_method

Call any static C# method via reflection.

```bash
# No args
curl localhost:7778/command -d '{"type":"execute_method","params":{"type_name":"UnityEditor.AssetDatabase","method_name":"Refresh"}}'

# With args
curl localhost:7778/command -d '{"type":"execute_method","params":{"type_name":"UnityEngine.Debug","method_name":"Log","args":["Hello from bridge"]}}'

# With explicit arg types (for overload disambiguation)
curl localhost:7778/command -d '{"type":"execute_method","params":{"type_name":"MyNamespace.MyClass","method_name":"DoThing","args":[42],"arg_types":["System.Int32"]}}'
```

**Params**: `type_name` (fully qualified type), `method_name` (string), `args?` ([...]), `arg_types?` (["System.Int32", ...] for overloads)

---

## Scripts Workflow

Write scripts with your native file tools (agent's own read/write), then refresh:

```bash
# After writing Assets/Scripts/Spinner.cs:
curl localhost:7778/command -d '{"type":"refresh"}'

# Check for compile errors
curl localhost:7778/events?since=0
curl localhost:7778/command -d '{"type":"read_console","params":{"count":5}}'

# Add the script to an object
curl localhost:7778/command -d '{"type":"add_component","params":{"target":"Cube","component_type":"Spinner"}}'
```
