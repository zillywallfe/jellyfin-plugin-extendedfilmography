# Publish your free Jellyfin repository

Repository: https://github.com/zillywallfe/jellyfin-plugin-extendedfilmography

This setup uses GitHub Pages to host both the catalog JSON and the small installable plugin ZIP. No separate release, custom domain, paid runner, token, or server is required. GitHub builds the DLL itself; do not upload your Jellyfin configuration or TMDb key.

## 1. Upload the source using your browser

1. Download and extract `extended-filmography-github-ready.zip` on Windows.
2. Open the extracted `jellyfin-plugin-extendedfilmography` folder. You should see `.github`, `src`, `verify`, `scripts`, `README.md`, and other project files.
3. Open your GitHub repository. If empty, click **uploading an existing file**. Otherwise choose **Add file → Upload files**.
4. Drag the CONTENTS of the extracted project folder into the upload area, including the `.github` folder. Do not upload the ZIP, and do not nest the whole project beneath another folder.
5. Commit the files to `main` with the message `Add Extended Filmography and catalog publishing`.
6. At the repository root, verify that you can open `.github/workflows/build.yml` and `src/` directly.

If GitHub will not accept the `.github` folder through drag-and-drop, use **Add file → Create new file**, enter `.github/workflows/build.yml` as the filename, and paste the contents of the included file. Commit it to `main`.

GitHub may run the build automatically after upload. This tests and packages the plugin but does not publish the catalog until you explicitly run the workflow in step 3.

## 2. Enable GitHub Pages

Open **Settings → Pages → Build and deployment → Source** and select **GitHub Actions**. Do not create one of the suggested starter workflows; this package already includes the workflow.

## 3. Publish

1. Open **Actions**.
2. If GitHub asks you to enable workflows for your repository, enable them.
3. Select **Build and publish Jellyfin repository**.
4. Click **Run workflow**, choose `main`, and run it.
5. Wait for both **build** and **deploy** to turn green.

The workflow runs the C# regression suite, compiles against Jellyfin 10.11.11, puts only the plugin DLL in a ZIP, calculates that ZIP's MD5 checksum as required by the Jellyfin catalog, and publishes both ZIP and catalog through Pages. It does not change your ThinkPad installation.

If it fails, open the failed step and send its output. Do not skip a failing build/test gate.

## 4. Verify the public files

After successful deployment, open:

https://zillywallfe.github.io/jellyfin-plugin-extendedfilmography/manifest.json

The JSON must show owner `zillywallfe`, version `1.0.1.0`, a real `sourceUrl`, and a 32-character checksum. Follow the sourceUrl to confirm that the plugin ZIP downloads.

The root `manifest.json` in the source repository intentionally has an empty `versions` list. It is a template. Use the published Pages URL above in Jellyfin, not the source file's raw GitHub URL.

## 5. Add it in Jellyfin

Open **Dashboard → Plugins → Repositories**, add a repository named **Extended Filmography**, and paste the published manifest URL above. Save, then refresh the plugin catalog. Extended Filmography should be listed with its owner and version.

Adding a repository does not repair missing plugin files or prove why the previous installation disappeared. Keep the current installation and backups until we have inspected its actual paths. If the plugin is truly absent, installing from this catalog can become the recovery route once we confirm there are no conflicting copies.

## Future publishing

Pushing changes to `main` runs tests and builds. Publishing requires **Actions → Run workflow** again. Before publishing a changed plugin DLL, increment the version consistently in the project, install.sh, meta.json, and build.yaml; otherwise Jellyfin may not detect an update. This initial catalog advertises the latest published version only.

The source currently remains at 1.0.1.0 because this preparation changes publishing infrastructure, not the plugin's browsing behavior.
