# Extended Filmography 1.0.1.0 — ThinkPad upgrade

This package is source code. The installer builds it in Docker and runs the verification harness before stopping Jellyfin. It targets Jellyfin 10.11.11 and .NET 9.

## Changes

- Query full library metadata for TMDb duplicate detection, with a one-minute library snapshot. Simultaneous first requests wait for the first snapshot instead of getting an empty set. Failed snapshots retry on the next request.
- Cache all eligible ranked credits. Apply the current library exclusion and then the display limit on both fresh and cached responses. A new cache-format stamp makes old cached selections obsolete without deleting the cache.
- New defaults: 50 titles, 10 minimum votes, no billing cutoff, and a one-episode fallback if a billing cutoff is subsequently enabled. Existing settings still equal to the old defaults migrate once. Customized values, API keys, enabled state, language, and other settings are retained.
- Install an explicit Active plugin manifest with the stable plugin ID. Back up stale versions and manifests outside plugin directories, and correct ownership.
- Clarify the feature's own Enable switch for comparisons. It takes effect on Save, needs no restart, and does not unload the plugin. Jellyfin's separate plugin-menu Disable action can remove the settings page because the plugin no longer runs. This release does not modify Jellyfin's dashboard or claim to fix its disabled-plugin listing behavior.

Duplicate detection uses media type + TMDb ID. Items without the correct TMDb provider ID cannot be reliably matched; no fuzzy title matching is used. Newly added media can take up to one minute to disappear from cached filmographies. The maximum remains subject to available credits and the client's paging behavior. Other filters (self appearances, talk/news/reality, unreleased titles, and acting-only) retain their settings.

## 1. Transfer from Windows Command Prompt

Save the attached archive to the Downloads folder used previously. Then:

```bat
scp "E:\Users\ljzui\Downloads\jellyfin-plugin-extendedfilmography-1.0.1.0.tar.gz" tinker@tinker-ThinkPad-P1:~/
ssh tinker@tinker-ThinkPad-P1
```

## 2. Extract outside Jellyfin's folders

On the ThinkPad:

```bash
mkdir -p ~/jellyfin-plugin-builds

tar -xzf ~/jellyfin-plugin-extendedfilmography-1.0.1.0.tar.gz \
  -C ~/jellyfin-plugin-builds

cd ~/jellyfin-plugin-builds/jellyfin-plugin-extendedfilmography-1.0.1.0

./install.sh
```

The installer asks for your sudo password for filesystem permissions. It builds and runs tests first. If either fails, Jellyfin stays running and the existing installation is untouched. Paste the error output rather than bypassing the checks.

After those checks pass it stops Jellyfin, moves all `Extended Filmography_*` installation folders from these locations into a dated backup, then installs and starts the server:

- `~/plex_config/jellyfin/data/plugins/` (the correct location)
- `~/plex_config/jellyfin/plugins/` (the original installer's incorrect location)

Backups go to `~/jellyfin-plugin-backups/<timestamp>/active/` and `legacy/`. Only this plugin's version folders are moved. The shared `configurations` folder and other plugins are retained. Ownership is copied from the existing AniDB directory, falling back to the plugins parent. If installation fails after stopping the server, backups remain and the script prints their location; correct the error before starting Jellyfin.

## 3. Verify

Once startup finishes:

```bash
docker logs --since 3m jellyfin 2>&1 \
  | grep -i -A8 -B2 'extendedfilmography\|Extended Filmography\|Error while starting server\|Startup complete'
```

Look for `Loaded plugin: "Extended Filmography" "1.0.1.0"` and `Startup complete`.

Open Dashboard → Plugins → Extended Filmography. Check that the new selection values are present. If you had customized them, they are preserved; you can manually set 50 titles, 10 votes, and billing cutoff 0. If previously switched off, check **Enable extended filmography** and Save.

Open an actor's page, wait a few seconds and reload if the cache is cold. Check a known movie that you own: it should occur only as the library item. Test both Jellyfin web and Neptune. For comparison, uncheck the feature's Enable checkbox in its settings and Save; leave Jellyfin's plugin-menu Disable action alone.

If a duplicate remains, provide the two titles, whether movie or series, and the TMDb ID shown under Edit metadata for the owned title. If the settings page disappears after using the feature checkbox and restarting, provide the fresh startup log and that plugin folder's `meta.json` (not the configuration XML, which contains your API key).

## 4. Clean up the old source/archive after the new version works

The installer has already removed previous installed versions from active plugin folders. These commands move any leftover source/build files into another backup outside Jellyfin. They do not delete your only copy:

```bash
cleanup_backup="$HOME/jellyfin-plugin-backups/old-source-$(date +%Y%m%d-%H%M%S)-$$"
mkdir -p "$cleanup_backup/build" "$cleanup_backup/plugin-source" "$cleanup_backup/archives"

if [ -d "$HOME/jellyfin-plugin-builds/jellyfin-plugin-extendedfilmography" ]; then
  mv "$HOME/jellyfin-plugin-builds/jellyfin-plugin-extendedfilmography" "$cleanup_backup/build/"
fi

if [ -d "$HOME/plex_config/jellyfin/data/plugins/jellyfin-plugin-extendedfilmography" ]; then
  sudo mv "$HOME/plex_config/jellyfin/data/plugins/jellyfin-plugin-extendedfilmography" "$cleanup_backup/plugin-source/"
  docker restart jellyfin
fi

if [ -f "$HOME/plex_config/jellyfin/data/plugins/jellyfin-plugin-extendedfilmography.tar.gz" ]; then
  mv "$HOME/plex_config/jellyfin/data/plugins/jellyfin-plugin-extendedfilmography.tar.gz" "$cleanup_backup/archives/"
fi
```

Do not move the new `jellyfin-plugin-extendedfilmography-1.0.1.0` source folder or delete the `configurations` folder.

## Manual recovery

If the new plugin prevents startup, stop Jellyfin and move `Extended Filmography_1.0.1.0` out of the active plugins directory into a backup. Start Jellyfin to restore service without this feature. To roll back, copy the previous plugin directory from the printed `active` backup into the correct plugins folder while Jellyfin is stopped, then start it. Selection settings migrated by this version remain saved; restore the former thresholds manually if desired.

## Alternate paths / build only

The defaults above are specific to your ThinkPad's known bind mount. For another installation set `JELLYFIN_PLUGINS_HOST` to the actual host plugins directory and `JELLYFIN_CONTAINER` to the container name. `JELLYFIN_BACKUP_HOST` can choose another backup root outside plugins. Verify that the reference folder owner is the account Jellyfin uses.

`./install.sh --build-only` runs tests and produces `artifacts/publish/Jellyfin.Plugin.ExtendedFilmography.dll` without touching Jellyfin.
