## Search Chapters Jellyfin plugin

Search Chapters is a Jellyfin server plugin that:
- **Searches embedded MP4 chapter titles** (e.g. created via `ffmpeg`).
- **Optionally uses fuzzy matching** for both item titles and chapter names.
- **Intercepts the built-in `/Items` search** so the normal search bar returns results ranked by chapter matches.
- **Decorates results** (taglines, overview, and name) with the matching chapter names so you can see them directly in search.

### Requirements

- **Jellyfin server**: 10.11.6 (target ABI `10.11.6.0`).
- **Build**: .NET SDK 9.0.

### Building

From the repository root:

```bash
dotnet build JellyfinSearchChapters/JellyfinSearchChapters.csproj -c Release
```

The plugin DLL will be in:

- `JellyfinSearchChapters/bin/Release/net9.0/JellyfinSearchChapters.dll`

For release builds, a ZIP is produced and referenced from `manifest.json` (see the `sourceUrl` entries for the current version).

### Installation

You can either:

- **Use the manifest (recommended)**  
  Add the raw `manifest.json` URL of this repo as a custom plugin repository in Jellyfin, then install **Search Chapters** from the catalog.  
  Jellyfin will validate the **MD5 checksum** from `manifest.json` against the ZIP you host on GitHub Releases.

- **Manual install**  
  1. Stop Jellyfin.
  2. Create a plugin directory (for example `Search Chapters_1.1.0.0`) under Jellyfin's `plugins` folder (e.g. `/config/plugins` in Docker).
  3. Copy `JellyfinSearchChapters.dll` (and PDB if you want symbols) into that directory.
  4. Start Jellyfin again.

### Configuration & behavior

- Plugin exposes a **configuration page** under **Dashboard → Plugins → Search Chapters**:
  - `EnableFuzzySearch` – when enabled, item titles and chapter names are matched fuzzily; when disabled, simple contains-matching is used.
- The plugin registers an **`ItemsSearchInterceptor`**:
  - Intercepts `GET /Items` requests that include `searchTerm` and no explicit `ids`.
  - Runs combined item-title + chapter-title search via `ILibraryManager` and chapter metadata.
  - Short-circuits the normal controller and returns a `QueryResult<BaseItemDto>` with items ordered by the combined score.
  - Populates:
    - `Taglines` with `"Chapter: <name>"`.
    - `Overview` with `Chapters: chapter1, chapter2, ...`.
    - `Name` with a suffix like `[chapter1, chapter2, chapter3]` so minimal clients still show chapter info.

### Versioning & manifest

- Assembly/file version is controlled by `Directory.Build.props`.
- Plugin metadata for Jellyfin (name, GUID, target ABI, etc.) is in `build.yaml`.
- The public plugin **repository manifest** is `manifest.json`, which:
  - Contains all released versions (`version`, `targetAbi`, `sourceUrl`, `checksum`, `changelog`, `timestamp`).
  - Is updated together with GitHub Releases so Jellyfin can install/upgrade Search Chapters via the catalog.

### Licensing (GPLv3)

This plugin is licensed under the **GNU General Public License v3.0 (GPLv3)**.

Due to how Jellyfin plugins work, when your plugin is compiled into a binary, it links against Jellyfin's GPLv3-licensed binary NuGet packages. As a result, the compiled plugin is also effectively GPLv3. You may use the provided GPLv3 license template for this project or another GPLv3-compatible open-source license that can be linked against GPLv3, but proprietary or source-unavailable distribution is not permitted.