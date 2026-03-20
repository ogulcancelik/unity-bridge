# Unity Bridge

Control Unity Editor with plain HTTP. One file, zero dependencies.

```bash
curl http://localhost:7778/command -d '{"type":"create_gameobject","params":{"name":"Cube","primitive_type":"Cube","position":[0,5,0]}}'
```

That's it. No MCP, no Python, no WebSocket middleware, no config files. Any tool that speaks HTTP can control Unity.

## Why

Existing solutions (MCP for Unity, etc.) require a Python runtime, a WebSocket server, MCP client configuration, and 70,000+ lines of code across 300 files.

Unity Bridge is **1 C# file**. It runs an HTTP server inside the Unity Editor. You send JSON, Unity does the thing.

```
MCP approach:   AI → MCP protocol → Python server → WebSocket → Unity plugin
This approach:  AI → HTTP → Unity
```

Works with any AI agent, any CLI tool, any language, any OS. If it has `curl`, it works.

## Comparison

vs [Unity MCP](https://github.com/CoplayDev/unity-mcp) (the most popular MCP-based solution):

|  | Unity Bridge | Unity MCP |
|---|---|---|
| **Files** | 1 | 776 |
| **C# lines** | 2,816 | 59,499 |
| **Python server** | none | 23,965 lines |
| **Dependencies** | none | Python, MCP SDK, Newtonsoft.Json |
| **Architecture** | HTTP → Unity | AI → MCP → Python → WebSocket → Unity |
| **Setup** | drop file or add git URL | install Unity package + Python server + configure MCP client |

### Feature coverage

| Feature | Unity Bridge | Unity MCP |
|---|---|---|
| Scene hierarchy & management | ✅ | ✅ |
| GameObject CRUD | ✅ | ✅ |
| Component add/remove | ✅ | ✅ |
| Property get/set (reflection) | ✅ | ✅ |
| Editor play/pause/stop | ✅ | ✅ |
| Asset search & create | ✅ | ✅ |
| Prefab create/instantiate | ✅ | ✅ |
| Selection get/set | ✅ | ✅ |
| Tags & layers | ✅ | ✅ |
| Console read | ✅ | ✅ |
| Execute menu items | ✅ | ✅ |
| Batch commands | ✅ | ✅ |
| Screenshots & capture | ✅ | ✅ |
| Events (polling) | ✅ | ✅ |
| Static method escape hatch | ✅ | ❌ |
| Animation / Cinemachine | ▫️ | ✅ |
| ProBuilder | ▫️ | ✅ |
| VFX / Particles | ▫️ | ✅ |
| UI Toolkit | ▫️ | ✅ |
| Procedural textures | ▫️ | ✅ |
| Script editing (in-Unity) | ▫️ | ✅ |
| Test runner | ▫️ | ✅ |

▫️ = not a dedicated tool, but achievable through `set_property` (reflection) and `execute_method` (static method calls). See [philosophy](#philosophy).

## Philosophy

The domain-specific tools that Unity MCP ships — animation controllers, ProBuilder mesh editing, Cinemachine rigs, VFX graphs, UI Toolkit — are convenience wrappers around Unity's own API. They're thousands of lines of code that duplicate what's already accessible through reflection and static method calls.

Modern AI agents know the Unity API. They know that `AnimatorController.CreateAnimatorControllerAtPath()` exists, that particle systems have a `main.startSpeed` property, that `PrefabUtility.SaveAsPrefabAsset()` takes a GameObject and a path. They don't need a hand-holding wrapper for every subsystem, they need a way to **reach into Unity and call things**.

Unity Bridge gives agents two primitives that cover the entire Unity API surface:

- **`set_property`** — get/set any property on any component via reflection. Vectors, colors, enums, object references — all auto-converted.
- **`execute_method`** — call any static C# method in any assembly. `AssetDatabase.Refresh()`, `AnimatorController.CreateAnimatorControllerAtPath()`, `EditorSceneManager.NewScene()` — anything.

Between these two and the core CRUD commands, there's nothing an agent can't do. It might take a few more calls than a dedicated wrapper, but it works for every Unity subsystem, including ones that don't exist yet.

The trade-off is intentional: **2,816 lines that cover everything** vs 60,000+ lines that cover specific subsystems with nicer ergonomics. We'd rather ship a small, reliable tool that agents can compose freely than maintain wrappers for every corner of Unity.

## Install

**Package Manager** → Add package from git URL:

```
https://github.com/ogulcancelik/unity-bridge.git
```

Or just copy `Editor/UnityBridge.cs` into your project's `Assets/Editor/` folder. Same thing.

## Quick Start

Bridge starts automatically on `http://localhost:7778`. Toggle via **Tools → Unity Bridge → Enabled**.

Browser-originated requests are intentionally blocked; use local tools/scripts/agents (`curl`, Pi, Claude Code, etc.), not a web page.

```bash
# Is it running?
curl http://localhost:7778/health

# Create something
curl localhost:7778/command -d '{"type":"create_gameobject","params":{"name":"Player","primitive_type":"Capsule","position":[0,1,0]}}'

# See what's in the scene
curl localhost:7778/command -d '{"type":"get_hierarchy"}'

# Poll for events (logs, compile status, play mode changes)
curl localhost:7778/events?since=0&limit=50
```

## For AI Agents

Point your agent to `/api` — it returns a full self-describing schema with every command, every parameter, conventions, and workflow patterns. The agent discovers everything at runtime, no static config needed.

```bash
curl http://localhost:7778/api
```

For the full command reference with examples, see [docs/commands.md](docs/commands.md).

## Commands

| Category | Commands |
|---|---|
| **Scene** | `get_hierarchy`, `get_scene_info`, `load_scene`, `save_scene` |
| **GameObjects** | `create_gameobject`, `modify_gameobject`, `delete_gameobject`, `duplicate_gameobject`, `find_gameobjects` |
| **Components** | `add_component`, `remove_component`, `get_components`, `set_property`, `get_property` |
| **Editor** | `editor_state`, `play`, `stop`, `pause`, `refresh`, `read_console`, `execute_menu_item` |
| **Assets** | `find_assets`, `create_asset` |
| **Screenshots** | `screenshot`, `capture`, `capture_sequence` |
| **Selection** | `get_selection`, `set_selection` |
| **Project** | `get_project_info`, `get_tags`, `get_layers`, `add_tag`, `add_layer` |
| **Prefabs** | `create_prefab`, `instantiate_prefab` |
| **Utility** | `batch`, `execute_method` |

## Architecture

```
Any HTTP client ──POST──► Unity Editor (HttpListener on :7778)
                          ├── Background thread receives request
                          ├── Queues command
                          ├── EditorApplication.update picks it up
                          ├── Executes on Unity main thread
                          └── Returns JSON result

Events ──GET──► Read directly from thread-safe ring buffer
                (no main thread round-trip needed)
```

One C# file. Runs inside the editor. No external processes. All mutations support undo (Ctrl+Z).

## Compatibility

- **Unity**: 2021.3 LTS and newer (including Unity 6)
- **Platforms**: Windows, macOS, Linux
- **Clients**: Local tools — Claude Code, Cursor, Windsurf, Pi, custom scripts, `curl`

## License

MIT
