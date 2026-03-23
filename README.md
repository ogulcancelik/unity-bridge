# Unity Bridge

Control Unity Editor with plain HTTP. One file, zero dependencies.

```bash
curl http://localhost:7778/command -d '{"type":"create_gameobject","params":{"name":"Cube","primitive_type":"Cube","position":[0,5,0]}}'
```

That's it. No MCP, no Python, no WebSocket middleware, no config files. Any tool that speaks HTTP can control Unity.

## Quick Start

**Package Manager** → Add package from git URL:

```
https://github.com/ogulcancelik/unity-bridge.git
```

Or just copy `Editor/UnityBridge.cs` into your project's `Assets/Editor/` folder. Same thing.

Bridge starts automatically on `http://localhost:7778`. Toggle via **Tools → Unity Bridge → Enabled**.

```bash
# Is it running?
curl http://localhost:7778/health

# Create something
curl localhost:7778/command -d '{"type":"create_gameobject","params":{"name":"Player","primitive_type":"Capsule","position":[0,1,0]}}'

# See what's in the scene
curl localhost:7778/command -d '{"type":"get_hierarchy"}'
```

## AI Agents

Tell your agent:

```
curl http://localhost:7778/api
```

That returns a self-describing schema — every command, every parameter, conventions, workflow patterns. The agent reads it, understands the API, and starts controlling Unity with curl. No tools to install, no config, no protocol.

To make it persistent, add to your project's `AGENTS.md` or `CLAUDE.md`:

```markdown
## Unity

Unity Bridge is running at localhost:7778 — it lets you control the Unity Editor over HTTP.

Run `curl http://localhost:7778/api` to get the full API schema with every command,
parameter, and convention. Use curl to send commands.
```

Or just mention it at the start of a conversation. Either way, one curl is the entire onboarding.

## How It's Different

MCP servers push tools into your agent's context at connection time. The agent sees them as native capabilities — but you need to install the server, configure the client, keep a sidecar process running, and those tool definitions live in context permanently.

Unity Bridge flips this. There's nothing to install on the agent side. The agent pulls the schema with one HTTP call when it needs it, then talks plain HTTP. No special protocol, no tool registration, no permanent context cost.

```
MCP:    install server → configure client → tools injected into context → agent calls tools
Bridge: agent curls /api → reads schema → agent curls /command
```

### vs Unity MCP

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

For the full command reference with examples, see [docs/commands.md](docs/commands.md).

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
